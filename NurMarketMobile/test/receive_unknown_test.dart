// 2026-10-27 (владелец: «Приёмка» показывает известные товары как «Неизвестный штрихкод»): код ищется на телефоне -> опечатка в
// контрольной цифре карточки -> сервер -> справочник NurCRM; вместо «Неизвестный» — «Найдено в справочнике», «Похоже на ваш»,
// «Опечатка в штрихкоде», честный текст. Пять кодов из расследования (каталог NBS). Сети и боевой базы нет: сервер и справочник — фейки.
import 'dart:io';

import 'package:drift/drift.dart' show Value;
import 'package:drift/native.dart';
import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/catalog/global_products.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/providers.dart';
import 'package:nurmarket/core/theme/theme.dart';
import 'package:nurmarket/features/stock/receive_lookup.dart';
import 'package:nurmarket/features/stock/stock_screens.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

import 'admin_stock_fixes_test.dart' show FakeApi;

class _Globals extends GlobalProducts {
  final Map<String, GlobalProduct> map;
  _Globals(this.map) : super(openFile: () async => File('unused'));
  @override
  Future<GlobalProduct?> find(String raw, {bool network = true}) async => map[raw];
}

class _Fixed extends SessionController {
  final Session s;
  _Fixed(this.s);
  @override
  Session? build() => s;
}

const _owner = Session(email: 'a', access: '', refresh: '', role: 'owner', isOwner: true, perms: []);
// кассир без права на товары: perms задан и пуст
const _noRights = Session(email: 'c', access: '', refresh: '', role: 'cashier', isOwner: false, perms: ['can_view_sales']);

// пять кодов из расследования
const _adygene = '4700000310326'; // есть в каталоге
const _asuOk = '4870001571030'; // в карточке ...039 — опечатка
const _winston = '48745000'; // EAN-8; в карточке внутренний код «0370»
const _pepsi = '4780022620177'; // у карточки «пепси 1.5» другой код
const _asuNew = '4870001159955'; // такого товара нет
const _nothing = '4600000000009'; // нигде нет

Future<void> _seed(AppDatabase db) async {
  Future<void> put(String id, String name, String code, [double qty = 5]) => db.upsertProduct(
    ProductsCompanion.insert(id: id, name: name, barcode: Value(code), quantity: Value(qty), searchKey: Value('${name.toLowerCase()} $code')),
  );
  await put('p1', 'Адыгене 1л', _adygene, 84);
  await put('p2', 'ASU Негаз 1 л клуб-киви', '4870001571039');
  await put('p3', 'ASU Негаз 1л персик', '4870001571528');
  await put('p4', 'Winston purple mix', '0370');
  await put('p5', 'пепси 1.5', '4870145005552');
}

final _globals = {
  _adygene: const GlobalProduct(barcode: _adygene, name: 'Адыгене 1л'),
  _asuOk: const GlobalProduct(barcode: _asuOk, name: 'ASU Негаз 1 л клуб-киви'),
  _winston: const GlobalProduct(barcode: _winston, name: 'Winston PurpleMIX'),
  _pepsi: const GlobalProduct(barcode: _pepsi, name: 'Пепси 1.5л'),
  _asuNew: const GlobalProduct(barcode: _asuNew, name: 'ASU Негаз 1л'),
};

void main() {
  late AppDatabase db;
  late FakeApi api;
  late List<String> log;
  // что отвечает сервер на поиск по коду (id -> карточка)
  late Map<String, Map<String, dynamic>> server;

  setUp(() async {
    db = AppDatabase(NativeDatabase.memory());
    await _seed(db);
    log = [];
    server = {};
    api = FakeApi((m, p, b, q) async {
      log.add('$m $p');
      if (m == 'GET' && p == 'api/main/products/list/') {
        final s = '${q?['search'] ?? ''}';
        return {
          'results': [
            for (final x in server.values)
              if ('${x['barcode']}' == s) x,
          ],
        };
      }
      final pm = RegExp(r'^api/main/products/(p\d+)/$').firstMatch(p);
      if (pm != null) {
        final id = pm.group(1)!;
        final x = server.putIfAbsent(id, () => _serverCard(id));
        if (m == 'GET') return Map<String, dynamic>.from(x);
        if (m == 'PATCH') {
          x.addAll((b as Map).cast<String, dynamic>());
          return Map<String, dynamic>.from(x);
        }
      }
      throw ApiException('не ожидали $m $p', status: 404);
    })..attach(_owner);
  });
  tearDown(() => db.close());

  group('поиск кода (resolveReceiveCode)', () {
    Future<ReceiveFind> find(String code, {bool network = true}) => resolveReceiveCode(db, api, _Globals(_globals), code, network: network);

    test('товар магазина — строка приёмки (с вариантами UPC-A <-> EAN-13)', () async {
      final f = await find(_adygene);
      expect(f.kind, ReceiveKind.found);
      expect(f.product!.name, 'Адыгене 1л');
      // тот же товар, код записан EAN-13 с нулём, а камера отдала UPC-A
      await db.upsertProduct(ProductsCompanion.insert(id: 'p6', name: 'Салфетки', barcode: const Value('0036000291452'), searchKey: const Value('салфетки')));
      expect((await find('036000291452')).kind, ReceiveKind.found, reason: 'камера отдала 12 цифр');
    });

    test('опечатка в контрольной цифре карточки — «Исправить»', () async {
      final f = await find(_asuOk);
      expect(f.kind, ReceiveKind.typo);
      expect(f.product!.id, 'p2');
    });

    test('нет у магазина, есть в справочнике, и ваш товар похож по названию — «Привязать»', () async {
      final w = await find(_winston);
      expect(w.kind, ReceiveKind.similar);
      expect(w.product!.name, 'Winston purple mix');
      expect(w.global!.name, 'Winston PurpleMIX');
      final p = await find(_pepsi);
      expect(p.kind, ReceiveKind.similar);
      expect(p.product!.name, 'пепси 1.5');
    });

    test('в справочнике есть, а вашего такого нет — «Завести товар» (вкус не путается: «ASU Негаз 1л» не персик)', () async {
      final f = await find(_asuNew);
      expect(f.kind, ReceiveKind.global);
      expect(f.global!.name, 'ASU Негаз 1л');
    });

    test('нигде нет — честное «не найден»', () async {
      expect((await find(_nothing)).kind, ReceiveKind.none);
    });

    test('товар, заведённый на сервере минуту назад, находится сервером', () async {
      server['p9'] = {'id': 'p9', 'name': 'Новый сок', 'barcode': _nothing, 'price': '50', 'quantity': 3};
      final f = await find(_nothing);
      expect(f.kind, ReceiveKind.found);
      expect(f.product!.name, 'Новый сок');
    });

    test('без сети: сервер не спрашивается, справочник — только локальный', () async {
      final f = await find(_asuNew, network: false);
      expect(f.kind, ReceiveKind.global);
      expect(log.where((e) => e.contains('products/list')), isEmpty);
    });
  });

  group('экран «Приёмка»', () {
    Future<void> pump(
      WidgetTester tester,
      List<String> codes, {
      Session session = _owner,
      Locale locale = const Locale('ru'),
      Size size = const Size(360, 2000),
      double scale = 1.0,
      bool dark = false,
    }) async {
      tester.view.physicalSize = size;
      tester.view.devicePixelRatio = 1;
      tester.platformDispatcher.textScaleFactorTestValue = scale;
      addTearDown(tester.view.reset);
      addTearDown(tester.platformDispatcher.clearAllTestValues);
      api.attach(session);
      final router = GoRouter(
        routes: [
          GoRoute(path: '/', builder: (_, _) => ReceiveScreen(debugScan: () async => codes)),
          GoRoute(path: '/admin/new-product', builder: (_, s) => Scaffold(body: Text('NEW ${s.uri.queryParameters['barcode']}'))),
        ],
      );
      await tester.pumpWidget(
        ProviderScope(
          overrides: [
            databaseProvider.overrideWithValue(db),
            apiProvider.overrideWithValue(api),
            sessionProvider.overrideWith(() => _Fixed(session)),
            globalProductsProvider.overrideWithValue(_Globals(_globals)),
          ],
          child: MaterialApp.router(
            routerConfig: router,
            theme: buildTheme(dark: dark),
            locale: locale,
            supportedLocales: AppLocalizations.supportedLocales,
            localizationsDelegates: const [AppLocalizations.delegate, GlobalMaterialLocalizations.delegate, GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate],
          ),
        ),
      );
      await tester.pump();
    }

    Future<void> settle(WidgetTester tester) async {
      for (var i = 0; i < 50; i++) {
        await tester.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 40)));
        await tester.pump();
      }
    }

    Future<void> scan(WidgetTester tester) async {
      await tester.tap(find.byIcon(Icons.qr_code_scanner_rounded));
      await settle(tester);
    }

    testWidgets('пять кодов: найден 1, опечатка 1, привязать 2, завести 1; «администратор получит» нет нигде', (tester) async {
      await pump(tester, [_adygene, _asuOk, _winston, _pepsi, _asuNew]);
      await scan(tester);
      // найденный — строка приёмки
      expect(find.text('Адыгене 1л'), findsOneWidget);
      // опечатка
      expect(find.textContaining('Опечатка в штрихкоде'), findsOneWidget);
      expect(find.byKey(const ValueKey('fix_$_asuOk')), findsOneWidget);
      // привязать (2) — справочник и «ваш» товар
      expect(find.textContaining('Найдено в справочнике NurCRM: «Winston PurpleMIX»'), findsOneWidget);
      expect(find.textContaining('Похоже на ваш «Winston purple mix»'), findsOneWidget);
      expect(find.textContaining('Найдено в справочнике NurCRM: «Пепси 1.5л»'), findsOneWidget);
      expect(find.textContaining('Похоже на ваш «пепси 1.5»'), findsOneWidget);
      expect(find.byKey(const ValueKey('bind_$_winston')), findsOneWidget);
      expect(find.byKey(const ValueKey('bind_$_pepsi')), findsOneWidget);
      // завести (1)
      expect(find.textContaining('Найдено в справочнике NurCRM: «ASU Негаз 1л»'), findsOneWidget);
      expect(find.byKey(const ValueKey('add_$_asuNew')), findsOneWidget);
      // «Неизвестный штрихкод» не показывается там, где код нашёлся
      expect(find.text('Неизвестный штрихкод'), findsNothing);
      expect(find.textContaining('администратор получит', findRichText: true), findsNothing);
    });

    testWidgets('неизвестный код: честный текст, повтор считается ×2, кнопка «Завести товар» открывает «Новый товар» с кодом', (tester) async {
      await pump(tester, [_nothing, _nothing]);
      await scan(tester);
      expect(find.text('Неизвестный штрихкод'), findsOneWidget);
      expect(find.textContaining('не найден ни в каталоге, ни в справочнике NurCRM'), findsOneWidget);
      expect(find.textContaining('код никуда не отправляется'), findsOneWidget);
      expect(find.textContaining('администратор получит'), findsNothing);
      expect(find.text('×2'), findsOneWidget, reason: 'повторный скан неизвестного кода считается');
      await tester.tap(find.byKey(const ValueKey('add_$_nothing')));
      await settle(tester);
      expect(find.text('NEW $_nothing'), findsOneWidget);
    });

    testWidgets('«Привязать штрихкод»: доп. код уходит в карточку, товар переходит в строки приёмки с накопленным количеством', (tester) async {
      await pump(tester, [_pepsi, _pepsi, _pepsi]);
      await scan(tester);
      expect(find.text('×3'), findsOneWidget);
      await tester.tap(find.byKey(const ValueKey('bind_$_pepsi')));
      await settle(tester);
      final patch = log.where((e) => e == 'PATCH api/main/products/p5/');
      expect(patch, hasLength(1));
      final alt = (server['p5']!['alternate_barcodes'] as List).cast<Map>();
      expect(alt.map((e) => e['barcode']), contains(_pepsi));
      expect(find.byKey(const ValueKey('unknown_$_pepsi')), findsNothing);
      expect(find.text('пепси 1.5'), findsOneWidget);
      expect(find.text('3'), findsOneWidget, reason: 'три скана неизвестного кода -> 3 штуки');
    });

    testWidgets('«Исправить»: основной код карточки меняется на верный', (tester) async {
      await pump(tester, [_asuOk]);
      await scan(tester);
      await tester.tap(find.byKey(const ValueKey('fix_$_asuOk')));
      await settle(tester);
      expect(server['p2']!['barcode'], _asuOk);
      expect(find.text('ASU Негаз 1 л клуб-киви'), findsOneWidget);
    });

    testWidgets('нет права на товары: кнопок «Завести / Привязать / Исправить» нет, есть пояснение', (tester) async {
      await pump(tester, [_asuOk, _pepsi, _asuNew, _nothing], session: _noRights);
      await scan(tester);
      expect(find.byKey(const ValueKey('fix_$_asuOk')), findsNothing);
      expect(find.byKey(const ValueKey('bind_$_pepsi')), findsNothing);
      expect(find.byKey(const ValueKey('add_$_asuNew')), findsNothing);
      expect(find.byKey(const ValueKey('add_$_nothing')), findsNothing);
      expect(find.textContaining('с правом «Склад»'), findsWidgets);
    });

    for (final (lang, dark) in [('ky', false), ('uz', true)]) {
      testWidgets('360 dp, $lang, шрифт 1,3${dark ? ', тёмная тема' : ''}: без переполнения, кнопки 48 dp', (tester) async {
        await pump(tester, [_asuOk, _winston, _pepsi, _asuNew, _nothing], locale: Locale(lang), scale: 1.3, dark: dark);
        await scan(tester);
        expect(tester.takeException(), isNull);
        for (final k in ['fix_$_asuOk', 'bind_$_winston', 'add_$_asuNew', 'add_$_nothing']) {
          final f = find.byKey(ValueKey(k));
          expect(f, findsOneWidget, reason: k);
          expect(tester.getSize(f).height, greaterThanOrEqualTo(48), reason: k);
        }
      });
    }
  });
}

Map<String, dynamic> _serverCard(String id) => switch (id) {
  'p5' => {'id': 'p5', 'name': 'пепси 1.5', 'barcode': '4870145005552', 'price': '60', 'purchase_price': '40', 'quantity': 5, 'unit': 'шт', 'alternate_barcodes': <Map>[]},
  'p2' => {'id': 'p2', 'name': 'ASU Негаз 1 л клуб-киви', 'barcode': '4870001571039', 'price': '60', 'purchase_price': '40', 'quantity': 5, 'unit': 'шт', 'alternate_barcodes': <Map>[]},
  _ => {'id': id, 'name': id, 'barcode': '', 'price': '1', 'purchase_price': '1', 'quantity': 0, 'unit': 'шт'},
};
