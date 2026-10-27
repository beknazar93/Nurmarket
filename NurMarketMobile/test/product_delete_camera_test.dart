// 2026-10-27 (мобилка, М3): жалобы владельца «в мобилке нет возможности удаления!» и «в товарах в редактировании нет
// возможности обучения умной камеры!». Проверяем: видимая кнопка «Удалить товар», право, остаток → ввод названия, очередь,
// ответы сервера 400/403/404/409/сеть, журнал; плитку «Умная камера» (эталоны: добавить, убрать, счётчик, сброс) и хранение
// эталонов по местам. Сервер NurCRM не трогается — подставной клиент, база в памяти, боевых DELETE нет.
import 'dart:convert';
import 'dart:io';

import 'package:drift/drift.dart' show Value;
import 'package:drift/native.dart';
import 'package:flutter/material.dart';
import 'package:flutter/rendering.dart';
import 'package:flutter/services.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_riverpod/misc.dart' show Override;
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/ai/product_refs.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/providers.dart';
import 'package:nurmarket/core/theme/theme.dart';
import 'package:nurmarket/core/util/audit.dart';
import 'package:nurmarket/features/admin/admin_repository.dart';
import 'package:nurmarket/features/stock/product_camera_tile.dart';
import 'package:nurmarket/features/stock/product_delete.dart';
import 'package:nurmarket/features/stock/product_edit.dart';
import 'package:nurmarket/features/stock/product_edit_repository.dart';
import 'package:nurmarket/features/stock/stock_repository.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

class _Fixed extends SessionController {
  final Session s;
  _Fixed(this.s);
  @override
  Session? build() => s;
}

const _owner = Session(email: 'o@x.kg', access: 'a', refresh: 'r', userId: 'u2', userName: 'Бакыт О.', role: 'owner', isOwner: true, companyName: 'Нур', cashboxId: 'b', cashboxName: 'Касса 1');
const _cashier = Session(email: 'c@x.kg', access: 'a', refresh: 'r', userId: 'u3', userName: 'Айгуль', role: 'cashier', cashboxId: 'b', cashboxName: 'Касса 1', perms: ['can_view_products']);
const _noStock = Session(email: 'n@x.kg', access: 'a', refresh: 'r', userId: 'u4', userName: 'Нурлан', role: 'cashier', cashboxId: 'b', cashboxName: 'Касса 1', perms: <String>[]);

class _Api extends ApiClient {
  _Api(this.handler) : super(SessionStore()) {
    attach(const Session(email: 'a@b.kg', access: 'a', refresh: 'r', userId: 'u1', cashboxId: 'box1', cashboxName: 'Касса'));
  }
  Future<dynamic> Function(String m, String p, Object? b) handler;
  final calls = <String>[];
  Future<dynamic> _h(String m, String p, Object? b) {
    calls.add('$m $p');
    return handler(m, p, b);
  }

  @override
  Future<dynamic> get(String path, {Map<String, dynamic>? query, Duration? timeout}) => _h('GET', path, null);
  @override
  Future<dynamic> post(String path, {Object? body, Map<String, String>? headers, Duration? timeout}) => _h('POST', path, body);
  @override
  Future<dynamic> patch(String path, {Object? body}) => _h('PATCH', path, body);
  @override
  Future<dynamic> delete(String path) => _h('DELETE', path, null);
}

Product _product({double quantity = 7, String id = 'p1', String name = 'Вода «Нур»'}) => Product(
  id: id,
  name: name,
  price: 85,
  wholesalePrice: 0,
  barcode: '4700000000011',
  altBarcodes: '',
  unit: 'шт',
  isWeight: false,
  isService: false,
  isFavorite: false,
  quantity: quantity,
  category: '',
  searchKey: name.toLowerCase(),
  promoRules: '',
  minQuantity: 0,
);

Map<String, dynamic> _serverProduct({String stock = '7', String name = 'Вода «Нур»'}) => {
  'id': 'p1',
  'name': name,
  'price': '85.00',
  'purchase_price': '60.00',
  'markup_percent': '41',
  'wholesale_price': '0',
  'quantity': stock,
  'unit': 'шт',
  'barcode': '4700000000011',
  'alternate_barcodes': [],
};

Uint8List _jpeg([int seed = 1]) => Uint8List.fromList([0xFF, 0xD8, 0xFF, seed, 2, 3, 4, 5, 6, 7, 8, 9]);

void _phone(WidgetTester t, {double scale = 1.0, double w = 360, double h = 780}) {
  t.view.physicalSize = Size(w * 2, h * 2);
  t.view.devicePixelRatio = 2.0;
  t.platformDispatcher.textScaleFactorTestValue = scale;
  final prev = FlutterError.onError;
  FlutterError.onError = (d) {
    if (d.exceptionAsString().contains('not supported by all of its localization delegates')) return; // kk/ky нет в Material
    prev?.call(d);
  };
  addTearDown(() {
    FlutterError.onError = prev;
    t.view.resetPhysicalSize();
    t.view.resetDevicePixelRatio();
    t.platformDispatcher.clearTextScaleFactorTestValue();
  });
}

Future<void> _fonts() async {
  final m = FontLoader('Montserrat');
  for (final w in ['Regular', 'Medium', 'SemiBold', 'Bold', 'ExtraBold']) {
    m.addFont(rootBundle.load('assets/fonts/Montserrat-$w.ttf'));
  }
  await m.load();
}

List<String> _clipped(WidgetTester t) {
  final out = <String>[];
  for (final e in find.byType(RichText).evaluate()) {
    final r = e.renderObject;
    if (r is RenderParagraph && r.didExceedMaxLines) out.add(r.text.toPlainText());
  }
  return out;
}

Widget _app(Widget home, {String lang = 'ru', bool dark = false, List<Override> overrides = const []}) => ProviderScope(
  overrides: overrides,
  child: MaterialApp(
    theme: buildTheme(dark: false),
    darkTheme: buildTheme(dark: true),
    themeMode: dark ? ThemeMode.dark : ThemeMode.light,
    locale: Locale(lang),
    supportedLocales: AppLocalizations.supportedLocales,
    localizationsDelegates: const [AppLocalizations.delegate, GlobalMaterialLocalizations.delegate, GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate],
    home: Scaffold(body: home),
  ),
);

/// Карточка открывается поверх «стартового» экрана, чтобы после удаления было куда вернуться.
Widget _launcher(Widget Function() screen) => Builder(
  builder: (c) => Center(child: ElevatedButton(onPressed: () => Navigator.of(c).push(MaterialPageRoute(builder: (_) => screen())), child: const Text('open'))),
);

/// Реальные файловые операции эталонов идут вне «поддельных» часов теста.
Future<void> _io(WidgetTester t) async {
  // цепочка «файл → база → файл»: каждому шагу нужен свой оборот настоящего цикла событий
  for (var i = 0; i < 24; i++) {
    await t.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 15)));
    await t.pump(const Duration(milliseconds: 10));
  }
  await t.pumpAndSettle();
}

Future<List<String>> _auditTexts() async => [for (final e in await Audit.read()) e.text];

void main() {
  late Directory tmp;

  setUpAll(() async {
    TestWidgetsFlutterBinding.ensureInitialized();
    tmp = Directory.systemTemp.createTempSync('nm_pdel_');
    Audit.testDirectory = tmp;
    ProductRefs.testDirectory = tmp;
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger.setMockMethodCallHandler(SystemChannels.platform, (c) async => null);
    await _fonts();
  });

  // ------------------------------------------------------------------ право и название
  group('право и подтверждение названием', () {
    test('canDeleteProducts: нет сессии — нельзя; владелец — можно; сотрудник — только с правом «Склад»', () {
      expect(canDeleteProducts(null), isFalse);
      expect(canDeleteProducts(_owner), isTrue);
      expect(canDeleteProducts(_cashier), isTrue);
      expect(canDeleteProducts(_noStock), isFalse, reason: 'профиль загружен, флага can_view_products нет');
    });

    test('название сверяется без регистра и пробелов по краям; пустое название не подтверждается', () {
      expect(deleteNameMatches('  вода «нур» ', 'Вода «Нур»'), isTrue);
      expect(deleteNameMatches('Вода', 'Вода «Нур»'), isFalse);
      expect(deleteNameMatches('', ''), isFalse);
    });
  });

  // ------------------------------------------------------------------ ответы сервера и журнал
  group('ProductDeleter: итоги и журнал (подставной сервер)', () {
    late AppDatabase db;
    late _Api api;
    late ProductDeleter deleter;
    setUp(() async {
      db = AppDatabase(NativeDatabase.memory());
      await db.upsertProduct(_product().toCompanion(true).copyWith(sku: const Value(null)));
      api = _Api((m, p, b) async => <String, dynamic>{});
      deleter = ProductDeleter(AdminRepository(api, db));
    });
    tearDown(() => db.close());

    Future<ProductDeleteResult> run() => deleter.run(_product(), logLabel: 'Удалить товар', failLabel: 'Не удалён', stock: 7);

    test('200: удалён на сервере и на телефоне, один DELETE, в журнале строка с остатком и номером', () async {
      final r = await run();
      expect(r.outcome, ProductDeleteOutcome.deleted);
      expect(api.calls, ['DELETE api/main/products/p1/']);
      expect(await db.productById('p1'), isNull);
      final log = await _auditTexts();
      expect(log.where((t) => t.contains('Удалить товар: Вода «Нур» · 7 шт · #p1')), hasLength(1));
    });

    test('403: нет права — товар остаётся на телефоне, в журнале отказ', () async {
      api.handler = (m, p, b) async => throw ApiException('Нет прав', status: 403);
      final r = await run();
      expect(r.outcome, ProductDeleteOutcome.forbidden);
      expect(await db.productById('p1'), isNotNull);
      expect((await _auditTexts()).any((t) => t.startsWith('Не удалён: Удалить товар: Вода «Нур»') && t.endsWith('· 403')), isTrue);
    });

    test('404 на обоих адресах: товара на сервере уже нет — убран только с телефона', () async {
      api.handler = (m, p, b) async => throw ApiException('Не найдено', status: 404);
      final r = await run();
      expect(r.outcome, ProductDeleteOutcome.alreadyGone);
      expect(r.removed, isTrue);
      expect(api.calls, ['DELETE api/main/products/p1/', 'DELETE api/main/products/list/p1/'], reason: 'запасной адрес тот же, что у кассы');
      expect(await db.productById('p1'), isNull);
    });

    test('400 и 422: сервер не разрешил — отказ с его текстом, товар остаётся', () async {
      for (final code in [400, 422]) {
        api.handler = (m, p, b) async => throw ApiException('Товар есть в продажах', status: code);
        final r = await run();
        expect(r.outcome, ProductDeleteOutcome.refused, reason: '$code');
        expect(r.message, 'Товар есть в продажах');
        expect(r.removed, isFalse);
      }
      expect(await db.productById('p1'), isNotNull);
    });

    test('409: это отказ сервера, а не обрыв связи (в ApiException 409 «временный» — для денег)', () async {
      api.handler = (m, p, b) async => throw ApiException('Конфликт', status: 409);
      final r = await run();
      expect(r.outcome, ProductDeleteOutcome.refused);
      expect(await db.productById('p1'), isNotNull);
    });

    test('нет связи, таймаут, 500, 429: «товар НЕ удалён», на телефоне остаётся', () async {
      for (final status in <int?>[null, 408, 429, 500, 503]) {
        api.handler = (m, p, b) async => throw ApiException('сбой', status: status);
        final r = await run();
        expect(r.outcome, ProductDeleteOutcome.offline, reason: '$status');
      }
      expect(await db.productById('p1'), isNotNull);
    });

    test('прочая ошибка (например 405 на обоих адресах) — «не удалён: …», без падения', () async {
      api.handler = (m, p, b) async => throw ApiException('Метод не разрешён', status: 405);
      final r = await run();
      expect(r.outcome, ProductDeleteOutcome.failed);
      expect(await db.productById('p1'), isNotNull);
    });
  });

  group('неотправленные операции с товаром', () {
    late AppDatabase db;
    setUp(() => db = AppDatabase(NativeDatabase.memory()));
    tearDown(() => db.close());

    Future<void> enqueue(String id, String kind, Object body, {String status = 'pending'}) =>
        db.enqueue(OutboxCompanion.insert(id: id, kind: kind, body: jsonEncode(body), createdAt: DateTime.now(), status: Value(status)));

    test('продажа и приёмка в очереди с этим товаром блокируют удаление; отправленные и чужие — нет', () async {
      await enqueue('s1', 'sale', {
        'items': [
          {'product': 'p1', 'quantity': 2},
          {'product': 'p9', 'quantity': 1},
        ],
      });
      await enqueue('r1', 'receive', {
        'lines': [
          {'productId': 'p1', 'qty': 5},
        ],
      });
      await enqueue('s2', 'sale', {
        'items': [
          {'product': 'p1'},
        ],
      }, status: 'sent');
      await enqueue('p1', 'sale', {
        'items': [
          {'product': 'p7'},
        ],
      });
      expect(await pendingOpsFor(db, 'p1'), 2, reason: 's1 и r1; отправленная s2 и операция с ключом p1 — не в счёт');
      expect(await pendingOpsFor(db, 'p7'), 1);
      expect(await pendingOpsFor(db, 'p5'), 0);
    });
  });

  // ------------------------------------------------------------------ эталоны камеры: хранение по местам
  group('ProductRefs: места под эталоны', () {
    late AppDatabase db;
    setUp(() => db = AppDatabase(NativeDatabase.memory()));
    tearDown(() => db.close());

    test('добавить в место 1 (второе): счётчик 1, место 0 пустое; убрать — товар снова не обучен', () async {
      expect(await ProductRefs.count(db, 'a'), 0);
      await ProductRefs.setSlot(db, 'a', 1, _jpeg(1));
      var slots = await ProductRefs.slots(db, 'a');
      expect(slots.map((s) => s != null), [false, true, false]);
      expect(await ProductRefs.count(db, 'a'), 1);
      expect(await ProductRefs.trainedIds(db), {'a'});
      expect((await ProductRefs.load(db, 'a')).length, 1, reason: 'продажа берёт только настоящие снимки');
      await ProductRefs.clearSlot(db, 'a', 1);
      slots = await ProductRefs.slots(db, 'a');
      expect(slots.every((s) => s == null), isTrue);
      expect(await ProductRefs.trainedIds(db), isEmpty);
    });

    test('замена снимка в том же месте: файл один, содержимое новое', () async {
      await ProductRefs.setSlot(db, 'b', 0, _jpeg(1));
      await ProductRefs.setSlot(db, 'b', 0, _jpeg(9));
      final s = await ProductRefs.slots(db, 'b');
      expect(s[0]![3], 9);
      expect(await ProductRefs.count(db, 'b'), 1);
    });

    test('старое обучение (save: 3 снимка) читается по местам; убрать одно — остальные целы; сброс убирает всё', () async {
      await ProductRefs.save(db, 'c', [_jpeg(1), _jpeg(2), _jpeg(3), _jpeg(4)]);
      expect(await ProductRefs.count(db, 'c'), 3, reason: 'сверка в продаже берёт не больше 3');
      await ProductRefs.clearSlot(db, 'c', 1);
      expect(await ProductRefs.count(db, 'c'), 2);
      expect((await ProductRefs.load(db, 'c')).map((b) => b[3]), [1, 3]);
      // повторное обучение поверх дырявого списка не падает
      await ProductRefs.save(db, 'c', [_jpeg(5)]);
      expect(await ProductRefs.count(db, 'c'), 1);
      await ProductRefs.clearAll(db, 'c');
      expect(await ProductRefs.trainedIds(db), isEmpty);
      await ProductRefs.clearAll(db, 'c'); // повтор безопасен
    });

    test('место вне диапазона и пустой код товара — ничего не ломают', () async {
      await ProductRefs.setSlot(db, 'd', 7, _jpeg());
      await ProductRefs.setSlot(db, '', 0, _jpeg());
      await ProductRefs.clearSlot(db, 'd', 0);
      expect(await ProductRefs.trainedIds(db), isEmpty);
    });
  });

  // ------------------------------------------------------------------ карточка товара: кнопка «Удалить» и плитка камеры
  group('карточка товара (ProductEditScreen)', () {
    late AppDatabase db;
    late _Api api;
    var stock = '7';
    setUp(() async {
      stock = '7';
      Audit.resetForTests(); // журнал из экрана пишется по «поддельным» часам — в этих тестах его не ждём (он проверен выше)
      db = AppDatabase(NativeDatabase.memory());
      await db.upsertProduct(_product().toCompanion(true).copyWith(sku: const Value(null)));
      api = _Api((m, p, b) async {
        if (m == 'GET' && p == 'api/main/products/p1/') return _serverProduct(stock: stock);
        if (m == 'DELETE') return null;
        throw ApiException('нет пути $m $p', status: 404);
      });
    });
    tearDown(() => db.close());

    Future<void> open(WidgetTester t, {Session session = _owner, String lang = 'ru', bool dark = false}) async {
      await t.pumpWidget(
        _app(
          _launcher(() => ProductEditScreen(product: _product())),
          lang: lang,
          dark: dark,
          overrides: [
            databaseProvider.overrideWithValue(db),
            apiProvider.overrideWithValue(api),
            sessionProvider.overrideWith(() => _Fixed(session)),
            productEditRepoProvider.overrideWithValue(ProductEditRepository(api, db, StockRepository(api, db))),
          ],
        ),
      );
      await t.tap(find.text('open'));
      await t.pumpAndSettle();
    }

    Future<void> toBottom(WidgetTester t, Finder f) async {
      await t.scrollUntilVisible(f, 300, scrollable: find.byType(Scrollable).first);
      await t.pumpAndSettle();
    }

    final deleteBtn = find.byKey(const Key('deleteProductButton'));
    final dialog = find.byKey(const Key('deleteProductDialog'));
    final confirm = find.byKey(const Key('deleteConfirm'));
    final nameField = find.byKey(const Key('deleteNameField'));

    testWidgets('владелец: красная кнопка «Удалить товар» видна внизу карточки (≥ 48 dp), «⋮» не нужна', (t) async {
      _phone(t);
      await open(t);
      await toBottom(t, deleteBtn);
      expect(deleteBtn, findsOneWidget);
      expect(find.descendant(of: deleteBtn, matching: find.text('Удалить товар')), findsOneWidget);
      expect(t.getSize(deleteBtn).height, greaterThanOrEqualTo(48));
      expect(t.getSize(deleteBtn).width, greaterThan(300), reason: 'во всю ширину');
    });

    testWidgets('остаток 7: предупреждение «На складе 7 шт», удалить можно только после ввода названия (регистр не важен)', (t) async {
      _phone(t);
      await open(t);
      await toBottom(t, deleteBtn);
      await t.tap(deleteBtn);
      await t.pumpAndSettle();
      expect(dialog, findsOneWidget);
      expect(find.byKey(const Key('deleteStockWarn')), findsOneWidget);
      expect(find.textContaining('На складе 7 шт'), findsOneWidget);
      expect(t.widget<FilledButton>(confirm).onPressed, isNull, reason: 'пока название не введено — удалить нельзя');
      await t.enterText(nameField, 'Вода');
      await t.pump();
      expect(t.widget<FilledButton>(confirm).onPressed, isNull, reason: 'неполное название');
      await t.enterText(nameField, 'ВОДА «нур»');
      await t.pump();
      expect(t.widget<FilledButton>(confirm).onPressed, isNotNull);
      expect(t.getSize(confirm).height, greaterThanOrEqualTo(48));
      expect(api.calls.where((c) => c.startsWith('DELETE')), isEmpty, reason: 'до подтверждения на сервер ничего не ушло');
      await t.tap(confirm);
      await _io(t);
      expect(api.calls.where((c) => c.startsWith('DELETE')), ['DELETE api/main/products/p1/']);
      expect(find.byType(ProductEditScreen), findsNothing, reason: 'после удаления карточка закрылась');
      expect(find.text('Товар удалён'), findsOneWidget);
      expect(await t.runAsync(() => db.productById('p1')), isNull);
    });

    testWidgets('остаток берётся свежий, с сервера (на телефоне 7, на сервере 0): предупреждения нет', (t) async {
      _phone(t);
      stock = '0';
      await open(t);
      await toBottom(t, deleteBtn);
      await t.tap(deleteBtn);
      await t.pumpAndSettle();
      expect(find.byKey(const Key('deleteStockWarn')), findsNothing);
      expect(nameField, findsNothing, reason: 'администратор, остатка нет — достаточно одного подтверждения');
      expect(t.widget<FilledButton>(confirm).onPressed, isNotNull);
      await t.tap(find.byKey(const Key('deleteCancel')));
      await t.pumpAndSettle();
      expect(dialog, findsNothing);
      expect(api.calls.where((c) => c.startsWith('DELETE')), isEmpty, reason: '«Отмена» — ничего не удалено');
      expect(find.byType(ProductEditScreen), findsOneWidget);
    });

    testWidgets('сотрудник с правом «Склад» (не администратор): название вводит всегда, даже при нулевом остатке', (t) async {
      _phone(t);
      stock = '0';
      await open(t, session: _cashier);
      await toBottom(t, deleteBtn);
      await t.tap(deleteBtn);
      await t.pumpAndSettle();
      expect(nameField, findsOneWidget);
      expect(t.widget<FilledButton>(confirm).onPressed, isNull);
    });

    testWidgets('без права «Склад»: кнопки «Удалить» нет ни внизу, ни в «⋮»', (t) async {
      _phone(t);
      await open(t, session: _noStock);
      expect(deleteBtn, findsNothing);
      await t.scrollUntilVisible(find.text('Сохранить'), 300, scrollable: find.byType(Scrollable).first);
      expect(deleteBtn, findsNothing);
      await t.tap(find.byType(PopupMenuButton<String>));
      await t.pumpAndSettle();
      expect(find.text('Удалить товар'), findsNothing);
      expect(find.text('Научить умную камеру: 3–5 фото + штрихкод'), findsOneWidget, reason: 'обучение осталось и в меню');
    });

    testWidgets('пункт «Удалить товар» в «⋮» работает через то же подтверждение', (t) async {
      _phone(t);
      await open(t);
      await t.tap(find.byType(PopupMenuButton<String>));
      await t.pumpAndSettle();
      await t.tap(find.text('Удалить товар').last);
      await t.pumpAndSettle();
      expect(dialog, findsOneWidget);
      expect(nameField, findsOneWidget, reason: 'остаток 7 — то же подтверждение названием');
    });

    Future<void> deleteWithName(WidgetTester t) async {
      await toBottom(t, deleteBtn);
      await t.tap(deleteBtn);
      await t.pumpAndSettle();
      await t.enterText(nameField, 'вода «нур»');
      await t.pump();
      await t.tap(confirm);
      await _io(t);
    }

    testWidgets('сервер 403: понятный текст про право, карточка остаётся, товар на телефоне цел', (t) async {
      _phone(t);
      await open(t);
      final inner = api.handler;
      api.handler = (m, p, b) => m == 'DELETE' ? throw ApiException('Forbidden', status: 403) : inner(m, p, b);
      await deleteWithName(t);
      expect(find.textContaining('Удалять товары может сотрудник с правом'), findsOneWidget);
      expect(find.byType(ProductEditScreen), findsOneWidget);
      expect(await t.runAsync(() => db.productById('p1')), isNotNull);
    });

    testWidgets('сервер 409: отказ с текстом сервера и советом списать остаток; карточка остаётся', (t) async {
      _phone(t);
      await open(t);
      final inner = api.handler;
      api.handler = (m, p, b) => m == 'DELETE' ? throw ApiException('Товар есть в продажах', status: 409) : inner(m, p, b);
      await deleteWithName(t);
      expect(find.textContaining('Сервер не разрешил удалить товар: Товар есть в продажах'), findsOneWidget);
      expect(find.textContaining('спишите остаток'), findsOneWidget);
      expect(find.byType(ProductEditScreen), findsOneWidget);
    });

    testWidgets('сервер 400: тоже отказ, без падения', (t) async {
      _phone(t);
      await open(t);
      final inner = api.handler;
      api.handler = (m, p, b) => m == 'DELETE' ? throw ApiException('detail: нельзя', status: 400) : inner(m, p, b);
      await deleteWithName(t);
      expect(find.textContaining('Сервер не разрешил удалить товар'), findsOneWidget);
      expect(t.takeException(), isNull);
    });

    testWidgets('сервер 404: «уже нет на сервере», товар убран с телефона, карточка закрылась', (t) async {
      _phone(t);
      await open(t);
      final inner = api.handler;
      api.handler = (m, p, b) => m == 'DELETE' ? throw ApiException('Not found', status: 404) : inner(m, p, b);
      await deleteWithName(t);
      expect(find.textContaining('Товара уже нет на сервере'), findsOneWidget);
      expect(find.byType(ProductEditScreen), findsNothing);
      expect(await t.runAsync(() => db.productById('p1')), isNull);
    });

    testWidgets('нет связи: «товар НЕ удалён», карточка остаётся', (t) async {
      _phone(t);
      await open(t);
      final inner = api.handler;
      api.handler = (m, p, b) => m == 'DELETE' ? throw ApiException('нет связи') : inner(m, p, b);
      await deleteWithName(t);
      expect(find.textContaining('товар НЕ удалён'), findsOneWidget);
      expect(find.byType(ProductEditScreen), findsOneWidget);
      expect(await t.runAsync(() => db.productById('p1')), isNotNull);
    });

    testWidgets('в очереди есть продажа с этим товаром: окно не открывается, на сервер ничего не уходит', (t) async {
      _phone(t);
      await open(t);
      await t.runAsync(() => db.enqueue(OutboxCompanion.insert(id: 'q1', kind: 'sale', body: jsonEncode({'items': [{'product': 'p1', 'quantity': 1}]}), createdAt: DateTime.now())));
      await toBottom(t, deleteBtn);
      await t.tap(deleteBtn);
      await t.pumpAndSettle();
      expect(dialog, findsNothing);
      expect(find.textContaining('неотправленные операции: 1'), findsOneWidget);
      expect(api.calls.where((c) => c.startsWith('DELETE')), isEmpty);
    });

    testWidgets('плитка «Умная камера» видна в карточке: «Эталонов: 0 из 3», «Обучить камеру» ≥ 48 dp, штрихкод', (t) async {
      _phone(t);
      await open(t);
      final tile = find.byKey(const Key('cameraTile'));
      await toBottom(t, tile);
      expect(tile, findsOneWidget);
      expect(find.byKey(const Key('camTileCount')), findsOneWidget);
      expect(find.text('Эталонов: 0 из 3'), findsOneWidget);
      expect(find.textContaining('Не обучена'), findsOneWidget);
      expect(t.getSize(find.byKey(const Key('camTrain'))).height, greaterThanOrEqualTo(48));
      expect(find.text('Штрихкод: 4700000000011'), findsOneWidget);
      expect(find.byKey(const Key('camReset')), findsNothing, reason: 'сбрасывать нечего');
    });
  });

  // ------------------------------------------------------------------ плитка отдельно: добавить, убрать, сбросить, обучить
  group('ProductCameraTile', () {
    late AppDatabase db;
    var shots = 0;
    var trained = 0;
    setUp(() async {
      Audit.resetForTests();
      db = AppDatabase(NativeDatabase.memory());
      shots = 0;
      trained = 0;
    });
    tearDown(() => db.close());

    Future<void> mount(WidgetTester t, {String barcode = '4700000000011', String lang = 'ru', bool dark = false, double scale = 1.0}) async {
      _phone(t, scale: scale);
      await t.pumpWidget(
        _app(
          SingleChildScrollView(
            padding: const EdgeInsets.all(16),
            child: ProductCameraTile(
              product: _product(),
              barcode: barcode,
              onScanBarcode: () {},
              onTrained: () => trained++,
              shoot: (c, title, hint) async => _jpeg(++shots),
              train: (c, ref, p) async {
                await ProductRefs.save(ref.read(databaseProvider), p.id, [_jpeg(7), _jpeg(8)]);
              },
            ),
          ),
          lang: lang,
          dark: dark,
          overrides: [databaseProvider.overrideWithValue(db)],
        ),
      );
      await _io(t);
    }

    testWidgets('снять в место 1 и 3: счётчик 2, превью, у каждого «Убрать» ≥ 48 dp; убрать — счётчик 1', (t) async {
      await mount(t);
      expect(find.text('Эталонов: 0 из 3'), findsOneWidget);
      await t.tap(find.byKey(const Key('camSlot0')));
      await _io(t);
      await t.tap(find.byKey(const Key('camSlot2')));
      await _io(t);
      expect(find.text('Эталонов: 2 из 3'), findsOneWidget);
      expect(find.textContaining('Обучена'), findsOneWidget);
      expect(find.byKey(const Key('camSlotImage0')), findsOneWidget);
      expect(find.byKey(const Key('camSlotImage1')), findsNothing);
      expect(find.byKey(const Key('camSlotImage2')), findsOneWidget);
      expect(t.getSize(find.byKey(const Key('camSlotRemove0'))).height, greaterThanOrEqualTo(48));
      expect(t.getSize(find.byKey(const Key('camSlot0'))).height, greaterThanOrEqualTo(48));
      expect(await t.runAsync(() => ProductRefs.count(db, 'p1')), 2, reason: 'сохранено на телефоне');
      await t.tap(find.byKey(const Key('camSlotRemove0')));
      await _io(t);
      expect(find.text('Эталонов: 1 из 3'), findsOneWidget);
      expect(find.byKey(const Key('camSlotImage0')), findsNothing);
      expect(await t.runAsync(() => ProductRefs.count(db, 'p1')), 1);
    });

    testWidgets('отмена съёмки (камера вернула null) — ничего не меняется', (t) async {
      _phone(t);
      await t.pumpWidget(
        _app(
          ProductCameraTile(product: _product(), barcode: '', onScanBarcode: () {}, shoot: (c, a, b) async => null),
          overrides: [databaseProvider.overrideWithValue(db)],
        ),
      );
      await _io(t);
      await t.tap(find.byKey(const Key('camSlot1')));
      await _io(t);
      expect(find.text('Эталонов: 0 из 3'), findsOneWidget);
      expect(find.text('Штрихкода нет — отсканируйте его'), findsOneWidget);
    });

    testWidgets('«Сбросить эталоны»: спрашивает, после «Да» все места пусты; «Отмена» — ничего', (t) async {
      await mount(t);
      await t.tap(find.byKey(const Key('camSlot0')));
      await _io(t);
      await t.tap(find.byKey(const Key('camSlot1')));
      await _io(t);
      await t.tap(find.byKey(const Key('camReset')));
      await t.pumpAndSettle();
      expect(find.byKey(const Key('camResetDialog')), findsOneWidget);
      await t.tap(find.text('Отмена'));
      await t.pumpAndSettle();
      expect(find.text('Эталонов: 2 из 3'), findsOneWidget);
      await t.tap(find.byKey(const Key('camReset')));
      await t.pumpAndSettle();
      await t.tap(find.byKey(const Key('camResetYes')));
      await _io(t);
      expect(find.text('Эталонов: 0 из 3'), findsOneWidget);
      expect(await t.runAsync(() => ProductRefs.trainedIds(db)), isEmpty);
    });

    testWidgets('«Обучить камеру»: запускается полный курс, затем места и счётчик перечитываются, карточка уведомлена', (t) async {
      await mount(t);
      await t.tap(find.byKey(const Key('camTrain')));
      await _io(t);
      expect(trained, 1);
      expect(find.text('Эталонов: 2 из 3'), findsOneWidget);
    });

    for (final lang in ['ky', 'uz']) {
      for (final dark in [false, true]) {
        testWidgets('360 dp, шрифт 1,3, $lang, ${dark ? 'тёмная' : 'светлая'} тема: без переполнения и обрезанных надписей', (t) async {
          await mount(t, lang: lang, dark: dark, scale: 1.3);
          await t.tap(find.byKey(const Key('camSlot0')));
          await _io(t);
          expect(t.takeException(), isNull);
          expect(_clipped(t), isEmpty);
          expect(t.getSize(find.byKey(const Key('camTrain'))).height, greaterThanOrEqualTo(48));
          expect(t.getSize(find.byKey(const Key('camSlotRemove0'))).height, greaterThanOrEqualTo(48));
          final l = lookupAppLocalizations(Locale(lang));
          expect(find.text(l.ctCount(1, 3)), findsOneWidget);
        });
      }
    }
  });

  // ------------------------------------------------------------------ окно удаления на узком экране
  for (final lang in ['ky', 'uz']) {
    testWidgets('окно удаления, 360 dp, шрифт 1,3, $lang, тёмная тема: помещается, кнопки ≥ 48 dp', (t) async {
      _phone(t, scale: 1.3, h: 640);
      final db = AppDatabase(NativeDatabase.memory());
      addTearDown(db.close);
      await t.runAsync(() => db.upsertProduct(_product().toCompanion(true).copyWith(sku: const Value(null))));
      final api = _Api((m, p, b) async => null);
      await t.pumpWidget(
        _app(
          Consumer(
            builder: (c, ref, _) => Center(
              child: ElevatedButton(onPressed: () => confirmAndDeleteProduct(c, ref, _product(name: 'Минералдуу суу «Нур» 1,5 л газдалбаган'), stock: 12), child: const Text('go')),
            ),
          ),
          lang: lang,
          dark: true,
          overrides: [databaseProvider.overrideWithValue(db), apiProvider.overrideWithValue(api), sessionProvider.overrideWith(() => _Fixed(_owner))],
        ),
      );
      await t.tap(find.text('go'));
      await t.pumpAndSettle();
      expect(find.byKey(const Key('deleteProductDialog')), findsOneWidget);
      expect(t.takeException(), isNull);
      expect(_clipped(t), isEmpty);
      expect(t.getSize(find.byKey(const Key('deleteConfirm'))).height, greaterThanOrEqualTo(48));
      expect(t.getSize(find.byKey(const Key('deleteCancel'))).height, greaterThanOrEqualTo(48));
    });
  }
}
