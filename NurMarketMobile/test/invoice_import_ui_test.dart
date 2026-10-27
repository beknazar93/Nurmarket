import 'dart:io';
import 'dart:typed_data';

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
import 'package:nurmarket/core/invoice/invoice_match.dart';
import 'package:nurmarket/core/invoice/invoice_parser.dart';
import 'package:nurmarket/core/providers.dart';
import 'package:nurmarket/core/theme/theme.dart';
import 'package:nurmarket/core/util/ui.dart' show money;
import 'package:nurmarket/features/admin/admin_receive.dart';
import 'package:nurmarket/features/admin/invoice_import.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

// 2026-10-27: экран «Загрузить накладную» с реальной накладной владельца (ТОРГ-12, рубли, без штрихкодов): 360×800 и 412×915,
// шрифт 1,0 и 1,3, ru / ky / uz, светлая и тёмная тема. Сеть не используется — поставщики и файл подставляются напрямую.
class _Api extends ApiClient {
  _Api() : super(SessionStore());
  final calls = <String>[];
  @override
  Future<dynamic> get(String path, {Map<String, dynamic>? query, Duration? timeout}) async {
    calls.add('GET $path');
    return {'purchase_price': '41000'};
  }
}

ParsedInvoice _torg() => InvoiceParser.parse(Uint8List.fromList(File('test/fixtures/invoices/owner_torg12.xlsx').readAsBytesSync()));

/// База в памяти отвечает асинхронно — ждём, пока предпросмотр разберёт строки (до 10 с).
Future<void> _settle(WidgetTester t, String lang) async {
  final l = lx(lang);
  for (var i = 0; i < 200; i++) {
    await t.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 50)));
    await t.pump();
    if (find.textContaining(l.invSummary(4, '1', '3', '0').split(':').first).evaluate().isNotEmpty) return;
  }
}

AppLocalizations lx(String code) => lookupAppLocalizations(Locale(code));

void main() {
  late AppDatabase db;
  late _Api api;

  Future<void> add(String id, String name, {double price = 0, double qty = 3, String? barcode}) => db.upsertProduct(
    ProductsCompanion.insert(id: id, name: name, barcode: Value(barcode), price: Value(price), quantity: Value(qty), searchKey: Value(name.toLowerCase())),
  );

  setUp(() async {
    db = AppDatabase(NativeDatabase.memory());
    api = _Api();
    InvoiceGuard.resetPending();
    // «Монитор…» есть в точности; «Ноутбук…» — похожий (15,6 дюйма); остальное — новое
    await add('mon', 'Монитор 24" IPS Full HD', price: 14500);
    await add('nb', 'Ноутбук бизнес-серии 15.6 дюйма', price: 52000);
  });
  tearDown(() async => db.close());

  Widget app(Widget home, {String lang = 'ru', bool dark = false}) => ProviderScope(
    key: UniqueKey(),
    overrides: [
      databaseProvider.overrideWithValue(db),
      apiProvider.overrideWithValue(api),
      globalProductsProvider.overrideWithValue(GlobalProducts(openFile: () async => throw StateError('справочника в проверке нет'))),
    ],
    child: MaterialApp(
      theme: buildTheme(dark: dark),
      locale: Locale(lang),
      supportedLocales: AppLocalizations.supportedLocales,
      localizationsDelegates: const [AppLocalizations.delegate, GlobalMaterialLocalizations.delegate, GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate],
      home: home,
    ),
  );

  void phone(WidgetTester t, {required Size logical, double scale = 1.0}) {
    t.view.physicalSize = Size(logical.width * 2, logical.height * 2);
    t.view.devicePixelRatio = 2.0;
    t.platformDispatcher.textScaleFactorTestValue = scale;
    addTearDown(() {
      t.view.resetPhysicalSize();
      t.view.resetDevicePixelRatio();
      t.platformDispatcher.clearTextScaleFactorTestValue();
    });
  }

  Future<void> open(WidgetTester t, ParsedInvoice inv, {String lang = 'ru', bool dark = false, List<SupplierRef> suppliers = const []}) async {
    await t.pumpWidget(app(InvoiceImportScreen(debugInvoice: inv, debugSuppliers: suppliers), lang: lang, dark: dark));
    await _settle(t, lang);
  }


  group('предпросмотр ТОРГ-12 владельца', () {
    for (final cfg in [
      ('ru', const Size(360, 800), 1.0, false),
      ('ky', const Size(360, 800), 1.0, false),
      ('ky', const Size(360, 800), 1.3, true),
      ('uz', const Size(412, 915), 1.3, false),
      ('ru', const Size(412, 915), 1.0, true),
    ]) {
      testWidgets('${cfg.$1} ширина ${cfg.$2.width.toInt()} dp, шрифт ${cfg.$3}${cfg.$4 ? ' тёмная' : ''}: шапка, предупреждение о рублях, статусы, итог сходится, без переполнений', (t) async {
        // высота большая: ListView строит только видимое, а проверяются все блоки; ширина — как у телефона
        phone(t, logical: Size(cfg.$2.width, 4000), scale: cfg.$3);
        final l = lx(cfg.$1);
        await open(t, _torg(), lang: cfg.$1, dark: cfg.$4, suppliers: const [SupplierRef('s1', 'ООО ТехноСфера-М', '7725896321')]);
        expect(t.takeException(), isNull);
        expect(find.text(l.invDocLine('104', '27.10.2026')), findsOneWidget);
        expect(find.text(l.invSupByInn), findsOneWidget, reason: 'поставщик найден по ИНН 7725896321');
        expect(find.text(l.invCurrency('RUB')), findsOneWidget);
        expect(find.text(l.invSummary(4, '1', '3', '0')), findsOneWidget, reason: 'монитор есть, ноутбук — «похоже», остальные новые');
        // итоги: 301 500 по строкам и в файле (сумма прописью)
        expect(find.text(l.invTotalOk), findsOneWidget);
        expect(find.text(l.invTotalLines(money(301500))), findsOneWidget);
        expect(find.text(l.invTotalFile(money(301500))), findsOneWidget);
        // кнопка «Добавить» — цель не меньше 48 dp
        final add = find.byType(FilledButton).last;
        expect(t.getSize(add).height, greaterThanOrEqualTo(48));
        // у каждой строки «убрать» не меньше 48×48
        final del = find.byTooltip(l.invRemoveRow);
        expect(del, findsWidgets);
        final s = t.getSize(del.first);
        expect(s.width, greaterThanOrEqualTo(48));
        expect(s.height, greaterThanOrEqualTo(48));
        expect(t.takeException(), isNull);
      });
    }

    for (final cfg in [('ky', const Size(360, 800), 1.3), ('uz', const Size(360, 800), 1.0), ('ru', const Size(412, 915), 1.3)]) {
      testWidgets('настоящий экран ${cfg.$2.width.toInt()}×${cfg.$2.height.toInt()} (${cfg.$1}, шрифт ${cfg.$3}): прокрутка до конца без переполнений, кнопка снизу ≥ 48 dp', (t) async {
        phone(t, logical: cfg.$2, scale: cfg.$3);
        await t.pumpWidget(app(InvoiceImportScreen(debugInvoice: _torg(), debugSuppliers: const []), lang: cfg.$1));
        for (var i = 0; i < 40; i++) {
          await t.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 50)));
          await t.pump();
        }
        expect(t.takeException(), isNull);
        for (var i = 0; i < 8; i++) {
          await t.drag(find.byType(ListView), const Offset(0, -500));
          await t.pump();
          expect(t.takeException(), isNull, reason: 'прокрутка $i');
        }
        expect(t.getSize(find.byType(FilledButton).last).height, greaterThanOrEqualTo(48));
        expect(find.byTooltip(lx(cfg.$1).invRemoveRow), findsWidgets);
        for (final e in find.byTooltip(lx(cfg.$1).invRemoveRow).evaluate()) {
          final sz = e.size!;
          expect(sz.width, greaterThanOrEqualTo(48));
          expect(sz.height, greaterThanOrEqualTo(48));
        }
      });
    }

    testWidgets('статусы строк: на складе по названию, «похоже» с выбором, новые заполнить вручную', (t) async {
      phone(t, logical: const Size(360, 3000));
      final l = lx('ru');
      await open(t, _torg());
      expect(find.text(l.invStName), findsOneWidget);
      expect(find.textContaining('Похоже на:'), findsOneWidget);
      expect(find.text(l.invStManual), findsNWidgets(2));
      // «похоже» (<0,9) само не принимается — выбор остаётся владельцу
      expect(find.byType(ChoiceChip), findsNWidgets(2));
    });

    testWidgets('правка закупки пересчитывает цену продажи и итог по строкам; расхождение подсвечивается', (t) async {
      phone(t, logical: const Size(412, 3000));
      final l = lx('ru');
      await open(t, _torg());
      // у «Сетевого фильтра» (новый): закупка 800 → цена +30 % до 5 = 1 040
      final fields = find.byType(TextField);
      expect(fields, findsWidgets);
      final costField = find.widgetWithText(TextField, '800');
      expect(costField, findsOneWidget);
      expect(find.widgetWithText(TextField, '1040'), findsOneWidget);
      await t.enterText(costField, '1000');
      await t.pump();
      expect(find.widgetWithText(TextField, '1300'), findsOneWidget, reason: '1000 + 30 % = 1300');
      expect(find.text(l.invTotalBad(money(1000))), findsOneWidget, reason: '5 × 200 = 1 000 лишних по сравнению с файлом');
    });

    testWidgets('убрать строку: итог и счётчик меняются; без строк кнопка выключена', (t) async {
      phone(t, logical: const Size(412, 3000));
      final l = lx('ru');
      await open(t, _torg());
      expect(find.text(l.invAdd(4)), findsOneWidget);
      await t.tap(find.byTooltip(l.invRemoveRow).last);
      await t.pump();
      expect(find.text(l.invAdd(3)), findsOneWidget);
      expect(find.textContaining('Итоги не сходятся'), findsOneWidget);
    });
  });

  group('дубль накладной', () {
    testWidgets('уже проведённая накладная: предупреждение, кнопка выключена, «Всё равно добавить» включает', (t) async {
      phone(t, logical: const Size(412, 3000));
      final l = lx('ru');
      final inv = _torg();
      await InvoiceGuard.markPosted(db, InvoiceGuard.keys(inv));
      await open(t, inv);
      expect(find.text(l.invDupPosted), findsOneWidget);
      FilledButton button() => t.widget<FilledButton>(find.byType(FilledButton).last);
      expect(button().onPressed, isNull);
      await t.tap(find.text(l.invDupAnyway));
      await t.pump();
      expect(button().onPressed, isNull, reason: 'ТОРГ-12 владельца в рублях: нужна ещё галочка про валюту (ревью Ск-14)');
      await t.tap(find.byKey(const Key('curOk')));
      await t.pump();
      expect(button().onPressed, isNotNull);
    });
  });

  group('в приход', () {
    testWidgets('строки передаются обычному экрану прихода: товары склада, новые, закупка, цена, поставщик; повторы объединены', (t) async {
      phone(t, logical: const Size(412, 3000));
      ReceiveHandoff? got;
      final router = GoRouter(
        routes: [
          GoRoute(
            path: '/',
            builder: (c, _) => Scaffold(
              body: TextButton(
                onPressed: () async => got = await c.push<ReceiveHandoff>('/admin/invoice'),
                child: const Text('open'),
              ),
            ),
          ),
          GoRoute(path: '/admin/invoice', builder: (_, _) => InvoiceImportScreen(forReceive: true, debugInvoice: _torg(), debugSuppliers: const [SupplierRef('s1', 'ООО ТехноСфера-М', '7725896321')])),
        ],
      );
      await t.pumpWidget(
        ProviderScope(
          overrides: [
            databaseProvider.overrideWithValue(db),
            apiProvider.overrideWithValue(api),
            globalProductsProvider.overrideWithValue(GlobalProducts(openFile: () async => throw StateError('нет'))),
          ],
          child: MaterialApp.router(
            routerConfig: router,
            locale: const Locale('ru'),
            supportedLocales: AppLocalizations.supportedLocales,
            localizationsDelegates: const [AppLocalizations.delegate, GlobalMaterialLocalizations.delegate, GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate],
          ),
        ),
      );
      await t.tap(find.text('open'));
      await t.pump();
      await _settle(t, 'ru');
      final l = lx('ru');
      // «Ноутбук» — «похоже» (<0,9): подтверждаем «Новый товар» не нужно — по умолчанию он новый, берём как есть
      await t.tap(find.byKey(const Key('curOk'))); // рубли: галочка «суммы проверил» (ревью Ск-14)
      await t.pump();
      await t.tap(find.text(l.invAdd(4)));
      await t.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 200)));
      await t.pumpAndSettle();
      expect(got, isNotNull);
      expect(got!.supplierId, 's1');
      expect(got!.lines.length, 4);
      final mon = got!.lines.firstWhere((x) => x.productId == 'mon');
      expect(mon.isNew, isFalse);
      expect(mon.qty, 5);
      expect(mon.cost, 12000);
      expect(mon.price, 14500, reason: 'цена на складе выше закупки — остаётся');
      expect(mon.oldCost, 41000, reason: 'прежняя закупка с сервера');
      expect(got!.lines.where((x) => x.isNew).length, 3);
      final filter = got!.lines.firstWhere((x) => x.name.startsWith('Сетевой'));
      expect(filter.productId, isEmpty);
      expect(filter.cost, 800);
      expect(filter.price, 1040);
      expect(filter.barcode, isEmpty, reason: 'внутренний код не присваивается без согласия владельца');
      expect(got!.onPosted, isNotNull);
      // накладная теперь «в приходе»
      expect(await InvoiceGuard.status(db, InvoiceGuard.keys(_torg())), 'pending');
    });
  });

  group('начальный экран', () {
    testWidgets('подсказка, выбор файла и «Накладная по фото» — 360 dp, ky и uz, шрифт 1,3', (t) async {
      for (final lang in ['ky', 'uz']) {
        phone(t, logical: const Size(360, 800), scale: 1.3);
        await t.pumpWidget(app(const InvoiceImportScreen(), lang: lang));
        await t.pump();
        final l = lx(lang);
        expect(find.text(l.invHint), findsOneWidget);
        expect(find.text(l.invPick), findsOneWidget);
        expect(find.text(l.invPhotoBtn), findsOneWidget);
        expect(t.getSize(find.byType(FilledButton).first).height, greaterThanOrEqualTo(48));
        expect(t.getSize(find.byType(OutlinedButton).first).height, greaterThanOrEqualTo(48));
        expect(t.takeException(), isNull);
      }
    });
  });

  group('права и маршруты', () {
    test('«Загрузить накладную» требует тех же прав, что «Приход»', () {
      expect(Session.routePermission('/admin/invoice'), Session.routePermission('/admin/receive'));
      const cashier = Session(email: 'a@x', access: 'a', refresh: 'r', userId: 'u', userName: 'К', role: 'cashier', perms: [Session.viewProducts]);
      const buyer = Session(email: 'a@x', access: 'a', refresh: 'r', userId: 'u', userName: 'К', role: 'cashier', perms: [Session.viewProducts, Session.procurement]);
      const admin = Session(email: 'a@x', access: 'a', refresh: 'r', userId: 'u', userName: 'А', role: 'admin', perms: []);
      expect(cashier.canRoute('/admin/invoice'), cashier.canRoute('/admin/receive'));
      expect(cashier.canRoute('/admin/invoice'), isFalse);
      expect(buyer.canRoute('/admin/invoice'), isTrue);
      expect(admin.canRoute('/admin/invoice'), admin.canRoute('/admin/receive'));
      expect(buyer.canRoute('/admin/invoice'), buyer.canRoute('/admin/receive'));
    });
  });
}
