// 2026-10-27: исправления по аудиту мобилки 27.10 — подтверждение аномальной цены (В-2), полоса режимов сканера и кнопка
// «Сканер для кассы» (С-1), «касса не найдена» / отказ кассы (С-3, С-4), проверка связи только в Wi-Fi и реже (С-7).
// Сеть и сервер не трогаются: касса и API — подставные, база в памяти.
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
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/link/kassa_link.dart';
import 'package:nurmarket/core/providers.dart';
import 'package:nurmarket/core/theme/theme.dart';
import 'package:nurmarket/core/util/audit.dart';
import 'package:nurmarket/core/util/ui.dart';
import 'package:nurmarket/features/profile/kassa_link_screen.dart';
import 'package:nurmarket/features/sale/sale_screen.dart';
import 'package:nurmarket/features/scan/kassa_scanner_mode.dart';
import 'package:nurmarket/features/scan/scan_screen.dart';
import 'package:nurmarket/features/stock/product_edit.dart';
import 'package:nurmarket/features/stock/product_edit_repository.dart';
import 'package:nurmarket/features/shift/shift_repository.dart';
import 'package:nurmarket/features/stock/stock_repository.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

const _key = 'AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8';
const _qr = 'nurmarket-link:v1?d=KASSA-1&h=192.168.1.20&p=47830&k=$_key&n=%D0%9E%D1%81%D0%BD%D0%BE%D0%B2%D0%BD%D0%B0%D1%8F&c=1a2b3c4d';

class _Fixed extends SessionController {
  final Session s;
  _Fixed(this.s);
  @override
  Session? build() => s;
}

const _owner = Session(email: 'o@x.kg', access: 'a', refresh: 'r', userId: 'u2', userName: 'Бакыт О.', role: 'owner', isOwner: true, companyName: 'Нур', cashboxId: 'b', cashboxName: 'Касса 1');

class _OpenShift extends ShiftController {
  @override
  ShiftState build() => const ShiftState(shift: ShiftInfo(id: 'sh1'));
}

class _Api extends ApiClient {
  _Api(this.handler) : super(SessionStore()) {
    attach(const Session(email: 'a@b.kg', access: 'a', refresh: 'r', userId: 'u1', cashboxId: 'box1', cashboxName: 'Касса'));
  }
  final Future<dynamic> Function(String m, String p, Object? b) handler;
  final calls = <String>[];
  final bodies = <Object?>[];
  Future<dynamic> _h(String m, String p, Object? b) {
    calls.add('$m $p');
    bodies.add(b);
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

ProductDraft _draft({double price = 85, double purchase = 60}) =>
    ProductDraft(name: 'Вода', unit: 'шт', category: '', barcode: '', alt: const [], price: price, purchase: purchase, wholesale: 0, minQty: null);

void _phone(WidgetTester t, {double scale = 1.0, double w = 360, double h = 640}) {
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

void main() {
  setUpAll(() async {
    TestWidgetsFlutterBinding.ensureInitialized();
    Audit.testDirectory = Directory.systemTemp.createTempSync('nm_audit1027_');
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger.setMockMethodCallHandler(SystemChannels.platform, (c) async => null);
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger.setMockMethodCallHandler(const MethodChannel('kg.nurcrm.nurmarket/beep'), (c) async => null);
    await _fonts();
  });

  // ------------------------------------------------------------------ В-2: аномальная цена
  group('В-2 аномальная цена: пороги', () {
    test('85 → 8 588 (×101): скачок вверх', () {
      final a = detectPriceAnomaly(_draft(), _draft(price: 8588))!;
      expect(a.issue, PriceIssue.jumpUp);
      expect(a.times, closeTo(101.04, .01));
    });

    test('ровно ×3 — не аномалия, чуть больше — аномалия (вверх и вниз)', () {
      expect(detectPriceAnomaly(_draft(price: 100, purchase: 0), _draft(price: 300, purchase: 0)), isNull);
      expect(detectPriceAnomaly(_draft(price: 100, purchase: 0), _draft(price: 301, purchase: 0))?.issue, PriceIssue.jumpUp);
      expect(detectPriceAnomaly(_draft(price: 300, purchase: 0), _draft(price: 100, purchase: 0)), isNull);
      expect(detectPriceAnomaly(_draft(price: 300, purchase: 0), _draft(price: 99, purchase: 0))?.issue, PriceIssue.jumpDown);
    });

    test('цена ниже закупки', () {
      final a = detectPriceAnomaly(_draft(price: 50, purchase: 45), _draft(price: 40, purchase: 45))!;
      expect(a.issue, PriceIssue.belowCost);
    });

    test('наценка выше 500 % (цена не менялась, а закупку занизили)', () {
      expect(detectPriceAnomaly(_draft(price: 60, purchase: 12), _draft(price: 60, purchase: 12)), isNull, reason: 'ничего не менялось');
      expect(detectPriceAnomaly(_draft(price: 70, purchase: 20), _draft(price: 70, purchase: 10))?.issue, PriceIssue.highMarkup); // 600 %
      expect(detectPriceAnomaly(_draft(price: 70, purchase: 20), _draft(price: 70, purchase: 11.7)), isNull); // 498 %
    });

    test('обычные правки не спрашивают; нулевая прежняя цена — только по наценке', () {
      expect(detectPriceAnomaly(_draft(price: 85, purchase: 60), _draft(price: 95, purchase: 65)), isNull);
      expect(detectPriceAnomaly(_draft(price: 0, purchase: 0), _draft(price: 500, purchase: 0)), isNull);
      expect(detectPriceAnomaly(_draft(price: 0, purchase: 10), _draft(price: 80, purchase: 10))?.issue, PriceIssue.highMarkup);
      expect(detectPriceAnomaly(_draft(), _draft(price: 0)), isNull, reason: 'нулевая цена — другая проверка');
    });
  });

  for (final lang in ['ru', 'ky', 'uz', 'en', 'tr']) {
    for (final issue in PriceIssue.values) {
      testWidgets('окно аномальной цены: $lang · ${issue.name} · 360 dp, шрифт 1,3 — без переполнений, кнопки ≥ 48 dp', (t) async {
        _phone(t, scale: 1.3);
        bool? result;
        await t.pumpWidget(
          _app(
            Builder(
              builder: (c) => Center(
                child: TextButton(
                  onPressed: () async => result = await confirmPriceAnomaly(c, PriceAnomaly(issue, was: 85, now: issue == PriceIssue.belowCost ? 40 : 8588, purchase: 60)),
                  child: const Text('open'),
                ),
              ),
            ),
            lang: lang,
          ),
        );
        await t.tap(find.text('open'));
        await t.pumpAndSettle();
        expect(t.takeException(), isNull);
        final l = t.element(find.byKey(const Key('priceAnomalyDialog'))).l;
        expect(find.text(l.peFixPrice), findsOneWidget);
        expect(find.text(l.peSaveAsIs), findsOneWidget);
        expect(t.getSize(find.byKey(const Key('priceAnomalyFix'))).height, greaterThanOrEqualTo(48));
        expect(t.getSize(find.byKey(const Key('priceAnomalyKeep'))).height, greaterThanOrEqualTo(48));
        await t.tap(find.byKey(const Key('priceAnomalyFix')));
        await t.pumpAndSettle();
        expect(result, isFalse);
      });
    }
  }

  testWidgets('текст окна: «Цена 8 588 вместо 85 — сохранить?»; «Сохранить как есть» → true', (t) async {
    _phone(t);
    bool? result;
    await t.pumpWidget(
      _app(
        Builder(
          builder: (c) => TextButton(onPressed: () async => result = await confirmPriceAnomaly(c, const PriceAnomaly(PriceIssue.jumpUp, was: 85, now: 8588, purchase: 60)), child: const Text('open')),
        ),
      ),
    );
    await t.tap(find.text('open'));
    await t.pumpAndSettle();
    expect(find.textContaining('Цена ${money(8588)} вместо ${money(85)} — сохранить?'), findsOneWidget);
    expect(find.textContaining('в 101 раз больше'), findsOneWidget);
    await t.tap(find.text('Сохранить как есть'));
    await t.pumpAndSettle();
    expect(result, isTrue);
  });

  group('карточка товара: «Сохранить» с аномальной ценой', () {
    late AppDatabase db;
    late _Api api;
    late Map<String, dynamic> server;

    setUp(() {
      db = AppDatabase(NativeDatabase.memory());
      server = {
        'id': 'p1',
        'name': 'Вода',
        'price': '85.00',
        'purchase_price': '60.00',
        'markup_percent': '41.67',
        'wholesale_price': '0',
        'quantity': '7',
        'unit': 'шт',
        'barcode': '4700000000011',
        'alternate_barcodes': [],
      };
      api = _Api((m, p, b) async {
        if (m == 'GET' && p == 'api/main/products/p1/') return Map<String, dynamic>.from(server);
        if (m == 'PATCH' && p == 'api/main/products/p1/') {
          server.addAll((b as Map).cast<String, dynamic>());
          return Map<String, dynamic>.from(server);
        }
        throw ApiException('нет пути $m $p', status: 404);
      });
    });
    tearDown(() => db.close());

    final product = Product(
      id: 'p1',
      name: 'Вода',
      price: 85,
      wholesalePrice: 0,
      barcode: '4700000000011',
      altBarcodes: '',
      unit: 'шт',
      isWeight: false,
      isService: false,
      isFavorite: false,
      quantity: 7,
      category: '',
      searchKey: 'вода',
      promoRules: '',
      minQuantity: 0,
    );

    Future<void> open(WidgetTester t) async {
      await t.pumpWidget(
        _app(
          ProductEditScreen(product: product),
          overrides: [
            databaseProvider.overrideWithValue(db),
            apiProvider.overrideWithValue(api),
            sessionProvider.overrideWith(() => _Fixed(_owner)),
            productEditRepoProvider.overrideWithValue(ProductEditRepository(api, db, StockRepository(api, db))),
          ],
        ),
      );
      await t.pumpAndSettle();
    }

    Future<void> tapSave(WidgetTester t) async {
      final save = find.widgetWithText(FilledButton, 'Сохранить');
      await t.scrollUntilVisible(save, 300, scrollable: find.byType(Scrollable).first);
      await t.pumpAndSettle();
      await t.tap(save);
      await t.pumpAndSettle();
    }

    testWidgets('85 → 8588: окно; «Исправить» — на сервер ничего не ушло, курсор в поле цены; «Сохранить как есть» — один PATCH', (t) async {
      _phone(t);
      await open(t);
      final price = find.byType(TextField).at(1);
      await t.enterText(price, '8588');
      await t.pump();
      await tapSave(t);
      expect(find.byKey(const Key('priceAnomalyDialog')), findsOneWidget);
      expect(find.textContaining('вместо ${money(85)}'), findsOneWidget);
      await t.tap(find.byKey(const Key('priceAnomalyFix')));
      await t.pumpAndSettle();
      expect(api.calls.where((c) => c.startsWith('PATCH')), isEmpty, reason: 'исправить — ничего не отправлено');
      final pf = FocusManager.instance.primaryFocus;
      final focused = pf?.context?.findAncestorWidgetOfExactType<EditableText>();
      expect(focused?.controller.text, '8588', reason: 'курсор вернулся в «Продажную цену»');

      await tapSave(t);
      await t.tap(find.byKey(const Key('priceAnomalyKeep')));
      await t.pumpAndSettle();
      final patches = api.calls.where((c) => c.startsWith('PATCH')).toList();
      expect(patches.length, 1);
      final body = api.bodies[api.calls.indexOf(patches.single)] as Map;
      expect(body['price'], '8588.00');
    });

    testWidgets('обычная правка цены 85 → 95: без вопроса, сразу PATCH', (t) async {
      _phone(t);
      await open(t);
      await t.enterText(find.byType(TextField).at(1), '95');
      await t.pump();
      await tapSave(t);
      expect(find.byKey(const Key('priceAnomalyDialog')), findsNothing);
      expect(api.calls.where((c) => c.startsWith('PATCH')).length, 1);
    });
  });


  // ------------------------------------------------------------------ С-2: остаток на плитке после своей продажи
  testWidgets('после проданного чека остаток на плитке — из базы телефона (было 48 → стало 46), без ожидания синхронизации', (t) async {
    _phone(t, h: 800);
    final db = AppDatabase(NativeDatabase.memory());
    addTearDown(db.close);
    await db.replaceProducts([ProductsCompanion.insert(id: 'c', name: 'Кола 1 л', price: const Value(85), quantity: const Value(48), searchKey: const Value('кола 1 л'))]);
    await t.pumpWidget(
      _app(
        const SaleScreen(),
        overrides: [
          databaseProvider.overrideWithValue(db),
          apiProvider.overrideWithValue(_Api((m, p, b) async => {'results': <dynamic>[]})),
          sessionProvider.overrideWith(() => _Fixed(_owner)),
          shiftProvider.overrideWith(_OpenShift.new),
        ],
      ),
    );
    await t.pumpAndSettle();
    expect(find.textContaining('48'), findsWidgets);
    final container = ProviderScope.containerOf(t.element(find.byType(SaleScreen)));
    final cola = (await t.runAsync<Product?>(() => db.productById('c')))!;
    container.read(cartProvider.notifier).add(cola);
    container.read(cartProvider.notifier).add(cola);
    await t.pump();
    // оплата: остаток вычитается в базе (SalesRepository._applyStock), затем чек очищается
    await t.runAsync(() => db.adjustStock('c', -2));
    container.read(cartProvider.notifier).clear();
    await t.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 100)));
    await t.pumpAndSettle();
    expect(find.textContaining('46'), findsWidgets, reason: 'плитка показывает новый остаток');
    expect(find.textContaining('48 '), findsNothing, reason: 'старый остаток не остался');
  });

  // ------------------------------------------------------------------ С-1: полоса режимов сканера
  for (final lang in ['ru', 'ky', 'uz']) {
    for (final scale in [1.0, 1.3]) {
      testWidgets('полоса режимов и кнопка «Сканер для кассы»: $lang, 360 dp, шрифт $scale — без переполнений', (t) async {
        _phone(t, scale: scale);
        ScanMode picked = ScanMode.sale;
        var kassaOn = false;
        await t.pumpWidget(
          _app(
            StatefulBuilder(
              builder: (c, set) => Container(
                color: Colors.black,
                padding: const EdgeInsets.all(14),
                child: Column(
                  mainAxisSize: MainAxisSize.min,
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: [
                    Row(
                      children: [
                        const SizedBox(width: 48, height: 48),
                        const SizedBox(width: 10),
                        Expanded(child: ScanModeBar(mode: picked, onSelect: (m) => set(() => picked = m))),
                        const SizedBox(width: 10),
                        const SizedBox(width: 48, height: 48),
                      ],
                    ),
                    const SizedBox(height: 8),
                    Align(
                      alignment: AlignmentDirectional.centerStart,
                      child: KassaModeChip(label: c.l.klScannerMode, active: kassaOn, onTap: () => set(() => kassaOn = !kassaOn)),
                    ),
                  ],
                ),
              ),
            ),
            lang: lang,
          ),
        );
        await t.pumpAndSettle();
        expect(t.takeException(), isNull);
        final l = t.element(find.byType(ScanModeBar)).l;
        // кнопка кассы — отдельной строкой, подпись целиком, высота ≥ 48
        expect(find.text(l.klScannerMode), findsOneWidget);
        expect(t.getSize(find.byType(KassaModeChip)).height, greaterThanOrEqualTo(48));
        expect(_clipped(t), isEmpty, reason: 'обрезано: ${_clipped(t)}');
        // все пять режимов есть в полосе; последний достижим прокруткой и нажимается
        for (final label in [l.toCheck, l.byLook, l.weighShort, l.checkPrice, l.receiving]) {
          expect(find.text(label), findsOneWidget, reason: label);
        }
        await t.ensureVisible(find.text(l.receiving));
        await t.pumpAndSettle();
        await t.tap(find.text(l.receiving));
        await t.pump();
        expect(picked, ScanMode.receive);
        await t.tap(find.byType(KassaModeChip));
        await t.pump();
        expect(kassaOn, isTrue);
      });
    }
  }

  // ------------------------------------------------------------------ С-3 / С-4: касса не найдена, отказ
  group('панель «Сканер для кассы» и экран «Связь с кассой»', () {
    late bool down;
    late String helloReply;
    late List<String> scans;

    KassaLink makeLink({Future<List<String>> Function()? subnets, DateTime Function()? clock}) => KassaLink(
      store: MemoryKassaLinkStore(),
      subnets: subnets ?? () async => <String>[],
      clock: clock,
      retryEvery: const Duration(milliseconds: 20),
      transport: (host, port, line, c, r) async {
        if (down) throw const KassaNetException('socket');
        final req = jsonDecode(line) as Map<String, dynamic>;
        if (req['op'] == 'hello') {
          return helloReply;
        }
        scans.add((jsonDecode(req['body'] as String) as Map)['code'] as String);
        return jsonEncode({'ok': true, 'code': 'added', 'name': 'Молоко'});
      },
    );

    setUp(() {
      down = false;
      scans = [];
      helloReply = jsonEncode({'ok': true, 'code': 'ok', 'kassa': 'Основная', 'company': '1a2b3c4d', 'shift_open': true});
    });

    Future<(KassaLink, KassaScannerController)> pairedIn(WidgetTester t, String mode) async {
      final link = makeLink();
      await t.runAsync(() async {
        await link.ensureLoaded();
        if (mode == 'notfound') down = true;
        if (mode == 'revoked') helloReply = jsonEncode({'ok': false, 'code': 'revoked'});
        if (mode == 'disabled') helloReply = jsonEncode({'ok': false, 'code': 'disabled'});
        if (mode == 'badsig') helloReply = jsonEncode({'ok': false, 'code': 'bad_sig'});
        if (mode == 'badtime') helloReply = jsonEncode({'ok': false, 'code': 'bad_time'});
        await link.pairFromQr(_qr);
      });
      final c = KassaScannerController(link: link);
      return (link, c);
    }

    Widget panel(KassaScannerController c, {String lang = 'ru', bool dark = false, VoidCallback? onSetup}) =>
        _app(Align(alignment: Alignment.bottomCenter, child: KassaScannerPanel(controller: c, onSetup: onSetup ?? () {})), lang: lang, dark: dark);

    testWidgets('«касса не найдена»: подсказка (включить «Принимать сканы с телефонов»), «Проверить связь» и «Сканировать QR заново»; «товар сразу попадёт в чек» нет', (t) async {
      _phone(t);
      final (link, c) = await pairedIn(t, 'notfound');
      var rescans = 0;
      await t.pumpWidget(panel(c, onSetup: () => rescans++));
      await t.pump();
      expect(link.status, KassaLinkStatus.notFound);
      expect(find.textContaining('Принимать сканы с телефонов'), findsOneWidget);
      expect(find.text('Проверить связь'), findsOneWidget);
      expect(find.text('Сканировать QR заново'), findsOneWidget);
      expect(find.textContaining('сразу попадёт в чек'), findsNothing);
      await t.tap(find.text('Сканировать QR заново'));
      expect(rescans, 1);
      c.dispose();
      link.dispose();
    });

    for (final (mode, status, hint, rescan) in [
      ('revoked', KassaLinkStatus.revoked, 'Касса не узнаёт этот телефон', true),
      ('badsig', KassaLinkStatus.rePair, 'Касса не узнаёт этот телефон', true),
      ('disabled', KassaLinkStatus.disabled, 'На кассе выключен приём', false),
      ('badtime', KassaLinkStatus.badTime, 'расходится больше чем на 10 минут', false),
    ]) {
      testWidgets('отказ кассы ($mode): честная подсказка${rescan ? ' и «Сканировать QR заново»' : ' и «Проверить связь»'}, без «товар сразу попадёт в чек»', (t) async {
        _phone(t);
        final (link, c) = await pairedIn(t, mode);
        var rescans = 0;
        await t.pumpWidget(panel(c, onSetup: () => rescans++));
        await t.pump();
        expect(link.status, status);
        expect(find.textContaining(hint), findsOneWidget);
        expect(find.textContaining('сразу попадёт в чек'), findsNothing);
        expect(find.text('Сканировать QR заново'), rescan ? findsOneWidget : findsNothing);
        expect(find.text('Проверить связь'), rescan ? findsNothing : findsOneWidget);
        if (rescan) {
          await t.tap(find.text('Сканировать QR заново'));
          expect(rescans, 1);
        }
        c.dispose();
        link.dispose();
      });
    }

    testWidgets('на связи: подсказка про чек есть, подсказки об отказе и кнопок восстановления нет', (t) async {
      _phone(t);
      final (link, c) = await pairedIn(t, 'online');
      await t.pumpWidget(panel(c));
      await t.pump();
      expect(find.textContaining('сразу попадёт в чек'), findsOneWidget);
      expect(find.text('Сканировать QR заново'), findsNothing);
      expect(find.text('Проверить связь'), findsNothing);
      c.dispose();
      link.dispose();
    });

    for (final lang in ['ru', 'ky', 'uz']) {
      for (final mode in ['notfound', 'revoked', 'disabled', 'badtime']) {
        testWidgets('панель при отказе: $mode · $lang · 360×640 dp, шрифт 1,3, тёмная — без переполнений, кнопки ≥ 48 dp', (t) async {
          _phone(t, scale: 1.3);
          final (link, c) = await pairedIn(t, mode);
          await t.pumpWidget(panel(c, lang: lang, dark: true));
          await t.pump();
          expect(t.takeException(), isNull);
          for (final b in [...find.byType(FilledButton).evaluate(), ...find.byType(OutlinedButton).evaluate()]) {
            expect(t.getSize(find.byWidget(b.widget)).height, greaterThanOrEqualTo(48));
          }
          // панель не выше 62 % экрана (прокручивается), камера остаётся видна
          expect(t.getSize(find.byType(KassaScannerPanel)).height, lessThanOrEqualTo(640 * .62 + 40));
          c.dispose();
          link.dispose();
        });
      }
    }

    testWidgets('скан при отказе кассы не отправляется: статус «не отправлен», на кассу запрос scan не уходит; связь вернулась — код уходит', (t) async {
      _phone(t);
      final (link, c) = await pairedIn(t, 'revoked');
      await t.runAsync(() async {
        await c.start();
        c.onCode('4870001234567');
        await Future<void>.delayed(const Duration(milliseconds: 60));
      });
      expect(scans, isEmpty);
      expect(c.lastStatus, KassaScanStatus.refused);
      expect(link.pendingCount, 0);
      // на кассе исправили: следующая проверка вернула «на связи»
      helloReply = jsonEncode({'ok': true, 'code': 'ok', 'kassa': 'Основная', 'company': '1a2b3c4d', 'shift_open': true});
      await t.runAsync(() async {
        await link.check(discover: false);
        c.onCode('4600000000011');
        await Future<void>.delayed(const Duration(milliseconds: 60));
      });
      expect(scans, ['4600000000011']);
      expect(c.lastStatus, KassaScanStatus.added);
      c.dispose();
      link.dispose();
    });

    testWidgets('экран «Связь с кассой», касса не найдена: блок «что проверить», «Проверить связь» и «Сканировать QR…»; ky/uz 360 dp 1,3 — без переполнений', (t) async {
      for (final lang in ['ru', 'ky', 'uz']) {
        _phone(t, scale: 1.3);
        down = true;
        final link = makeLink();
        await t.runAsync(() async {
          await link.ensureLoaded();
          await link.pairFromQr(_qr);
        });
        await t.pumpWidget(_app(KassaLinkScreen(link: link), lang: lang, overrides: [sessionProvider.overrideWith(() => _Fixed(_owner))]));
        await t.pump(const Duration(milliseconds: 50));
        expect(t.takeException(), isNull, reason: lang);
        expect(find.byKey(const Key('klHelp')), findsOneWidget, reason: lang);
        final l = t.element(find.byKey(const Key('klHelp'))).l;
        expect(find.text(l.klHelpNotFound), findsOneWidget);
        expect(find.text(l.klCheckNow), findsOneWidget);
        await t.scrollUntilVisible(find.text(l.klScanQr), 200);
        expect(find.text(l.klScanQr), findsOneWidget);
        await t.pumpWidget(const SizedBox());
        link.dispose();
      }
    });

    test('a11yJoin: без «….» на стыке', () {
      expect(a11yJoin(['Проверка связи…', 'Касса «Основная»']), 'Проверка связи. Касса «Основная»');
      expect(a11yJoin(['На связи', '', 'В очереди: 2']), 'На связи. В очереди: 2');
    });

    // ---------------------------------------------------------------- С-7: только Wi-Fi и реже
    test('подсети: только Wi-Fi (wlan/eth/en/ap), мобильные rmnet*/ccmni*/pdp_ip*/tun*, loopback и 169.254 — нет', () {
      expect(isWifiInterface('wlan0'), isTrue);
      expect(isWifiInterface('swlan0'), isTrue);
      expect(isWifiInterface('eth0'), isTrue);
      expect(isWifiInterface('en0'), isTrue);
      expect(isWifiInterface('ap0'), isTrue);
      for (final n in ['rmnet_data0', 'rmnet0', 'v4-rmnet_data1', 'ccmni0', 'pdp_ip0', 'tun0', 'ppp0', 'dummy0', 'lo', 'p2p0']) {
        expect(isWifiInterface(n), isFalse, reason: n);
      }
      final prefixes = wifiSubnetPrefixes([
        ('wlan0', ['192.168.1.55']),
        ('rmnet_data0', ['100.64.12.9']),
        ('eth0', ['169.254.3.4', '192.168.1.77']),
        ('tun0', ['10.8.0.2']),
      ]);
      expect(prefixes, ['192.168.1']);
      expect(wifiSubnetPrefixes([('rmnet_data0', ['100.64.12.9'])]), isEmpty, reason: 'мобильный интернет — перебора нет');
    });

    test('фоновый поиск кассы: паузы 30 с → 2 мин → 5 мин, «Проверить связь» ищет всегда, ответ кассы сбрасывает счётчик', () async {
      expect(KassaLink.rediscoverPause(0), const Duration(seconds: 30));
      expect(KassaLink.rediscoverPause(1), const Duration(minutes: 2));
      expect(KassaLink.rediscoverPause(2), const Duration(minutes: 5));
      expect(KassaLink.rediscoverPause(9), const Duration(minutes: 5));

      var now = DateTime(2026, 10, 27, 12);
      var searches = 0;
      down = true;
      final link = makeLink(
        clock: () => now,
        subnets: () async {
          searches++;
          return ['192.168.1'];
        },
      );
      await link.pairFromQr(_qr); // поиск №1 (не фоновый)
      expect(searches, 1);
      Future<void> bg(Duration after) async {
        now = now.add(after);
        await link.check(background: true);
      }

      await bg(const Duration(seconds: 31)); // пауза после одного неудачного поиска — 2 мин: поиска нет
      expect(searches, 1);
      await bg(const Duration(minutes: 2)); // прошло > 2 мин: поиск №2
      expect(searches, 2);
      await bg(const Duration(minutes: 3)); // после двух неудач пауза 5 мин: 3 мин мало
      expect(searches, 2);
      await bg(const Duration(minutes: 3)); // всего 6 мин: поиск №3
      expect(searches, 3);
      await link.check(); // кнопка «Проверить связь» — ищет всегда
      expect(searches, 4);
      down = false; // касса вернулась на прежнем адресе
      expect(await link.check(background: true), KassaLinkStatus.online);
      down = true;
      await bg(const Duration(seconds: 31)); // ответ кассы сбросил счётчик — снова 30 с
      expect(searches, 5);
      link.dispose();
    });

    test('проверка связи в режиме сканера — раз в 25 с, а не в 8', () {
      expect(KassaScannerController.beatEvery, const Duration(seconds: 25));
    });
  });
}

