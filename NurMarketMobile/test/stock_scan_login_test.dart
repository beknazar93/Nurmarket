// 2026-10-26 (живой аудит, П7–П9): склад и товар, сканер камеры (полоса режимов), вход. Сервер не трогается — фейки.
import 'package:drift/drift.dart' show Value, driftRuntimeOptions;
import 'package:drift/native.dart';
import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_riverpod/misc.dart' show Override;
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/providers.dart';
import 'package:nurmarket/core/theme/theme.dart';
import 'package:nurmarket/features/auth/login_screen.dart';
import 'package:nurmarket/features/scan/scan_screen.dart';
import 'package:nurmarket/features/stock/barcode_label.dart';
import 'package:nurmarket/features/stock/stock_screens.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

class _Api extends ApiClient {
  _Api(this.handler) : super(SessionStore());
  final Future<dynamic> Function(String path, Map<String, dynamic>? query) handler;
  @override
  Future<dynamic> get(String path, {Map<String, dynamic>? query, Duration? timeout}) => handler(path, query);
}

Widget _app(Widget home, {List<Override> overrides = const [], String lang = 'ru'}) => ProviderScope(
  overrides: overrides,
  child: MaterialApp(
    theme: buildTheme(dark: false),
    locale: Locale(lang),
    supportedLocales: AppLocalizations.supportedLocales,
    localizationsDelegates: const [AppLocalizations.delegate, GlobalMaterialLocalizations.delegate, GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate],
    home: home,
  ),
);

void _phone(WidgetTester t, {double scale = 1.0}) {
  t.view.physicalSize = const Size(720, 1280); // 360×640 dp
  t.view.devicePixelRatio = 2.0;
  t.platformDispatcher.textScaleFactorTestValue = scale;
  final prev = FlutterError.onError;
  FlutterError.onError = (d) {
    if (d.exceptionAsString().contains('not supported by all of its localization delegates')) return;
    prev?.call(d);
  };
  addTearDown(() {
    FlutterError.onError = prev;
    t.view.resetPhysicalSize();
    t.view.resetDevicePixelRatio();
    t.platformDispatcher.clearTextScaleFactorTestValue();
  });
}

void main() {
  late AppDatabase db;
  setUp(() {
    driftRuntimeOptions.dontWarnAboutMultipleDatabases = true;
    db = AppDatabase(NativeDatabase.memory());
  });
  tearDown(() => db.close());

  Future<void> put(String id, String name, {String barcode = '', String alt = '', double qty = 5}) => db.upsertProduct(
    ProductsCompanion.insert(
      id: id,
      name: name,
      barcode: Value(barcode.isEmpty ? null : barcode),
      altBarcodes: Value(alt),
      quantity: Value(qty),
      searchKey: Value(name.toLowerCase()),
    ),
  );

  group('П7: приёмка и ревизия — упаковка', () {
    test('обычный код +1, штрихкод упаковки +N, неизвестный — null', () async {
      await put('p1', 'Вода', barcode: '4600000000003', alt: '4600000000010|12');
      expect((await receiveScanHit(db, '4600000000003'))!.qty, 1);
      final pack = await receiveScanHit(db, '4600000000010');
      expect(pack!.product.id, 'p1');
      expect(pack.qty, 12);
      expect(await receiveScanHit(db, '9999999999999'), isNull);
    });
  });

  group('П7: проверка кода по серверу', () {
    test('код есть у другого товара — возвращается название; свой товар не считается', () async {
      final api = _Api((path, q) async => {'results': [{'id': 'x', 'name': 'Чужой', 'barcode': '4600000000003'}]});
      expect(await serverBarcodeOwner(api, '4600000000003'), 'Чужой');
      expect(await serverBarcodeOwner(api, '4600000000003', exceptProductId: 'x'), isNull);
    });

    test('чужие товары без этого кода, ошибка или таймаут — свободен, ввод не блокируется', () async {
      expect(await serverBarcodeOwner(_Api((p, q) async => {'results': [{'id': 'x', 'name': 'Другой', 'barcode': '1'}]}), '4600000000003'), isNull);
      expect(await serverBarcodeOwner(_Api((p, q) async => throw ApiException('Нет связи')), '4600000000003'), isNull);
    });

    test('в запросе — параметр barcode', () async {
      Map<String, dynamic>? seen;
      await serverBarcodeOwner(_Api((p, q) async {
        seen = q;
        return {'results': []};
      }), '4600000000003');
      expect(seen!['barcode'], '4600000000003');
    });
  });

  testWidgets('П7: PDF этикетки собирается (EAN-13, Code128, кириллица)', (t) async {
    await t.runAsync(() async {
      final a = await labelPdfBytes(name: 'Вода «Нур» 0,5 л', code: '4600000000003', price: 45.5, currency: 'сом');
      final b = await labelPdfBytes(name: 'Товар', code: 'AB-123', price: 0);
      expect(String.fromCharCodes(a.take(4)), '%PDF');
      expect(String.fromCharCodes(b.take(4)), '%PDF');
    });
  });

  for (final scale in [1.0, 1.3]) {
    testWidgets('П7: склад — нет плавающей «Новый товар», чипы не обрезаны (шрифт $scale)', (t) async {
      _phone(t, scale: scale);
      await put('p1', 'Вода', barcode: '4600000000003');
      await t.pumpWidget(_app(const Scaffold(body: StockScreen()), overrides: [databaseProvider.overrideWithValue(db)]));
      await t.runAsync(() => Future.delayed(const Duration(milliseconds: 300)));
      await t.pump();
      expect(t.takeException(), isNull);
      expect(find.byType(FloatingActionButton), findsNothing);
      final l = lookupAppLocalizations(const Locale('ru'));
      expect(find.text(l.newProduct), findsOneWidget);
      final bar = find.ancestor(of: find.widgetWithText(ChoiceChip, l.filterAll), matching: find.byType(SizedBox)).first;
      expect(t.getSize(bar).height, greaterThanOrEqualTo(48));
    });
  }

  group('П8: режимы сканера', () {
    // 2026-10-29: полоса режимов заменена селектом ScanModeSelect (поле + нижний лист)
    for (final lang in ['ru', 'ky']) {
      testWidgets('$lang, 360 dp: поле режима ≥ 48 dp, шесть пунктов в листе, выбор «Приёмка»', (t) async {
        _phone(t, scale: 1.3);
        final picked = <ScanMode>[];
        await t.pumpWidget(_app(
          Scaffold(
            backgroundColor: Colors.black,
            body: Row(children: [Expanded(child: ScanModeSelect(mode: ScanMode.sale, onSelect: picked.add, onKassa: () {}))]),
          ),
          lang: lang,
        ));
        await t.pump();
        expect(t.takeException(), isNull);
        final l = lookupAppLocalizations(Locale(lang));
        expect(t.getSize(find.byType(ScanModeSelect)).height, greaterThanOrEqualTo(48));
        expect(find.byType(SingleChildScrollView), findsNothing, reason: 'горизонтальной прокрутки нет');
        await t.tap(find.byType(ScanModeSelect));
        await t.pumpAndSettle();
        final inSheet = find.descendant(of: find.byType(BottomSheet), matching: find.byType(InkWell));
        expect(inSheet, findsNWidgets(6));
        for (final e in inSheet.evaluate()) {
          expect(t.getSize(find.byElementPredicate((x) => x == e)).height, greaterThanOrEqualTo(48));
        }
        await t.tap(find.descendant(of: find.byType(BottomSheet), matching: find.text(l.receiving)));
        await t.pumpAndSettle();
        expect(picked, [ScanMode.receive]);
        expect(t.takeException(), isNull);
      });
    }
  });

  group('П9: вход', () {
    testWidgets('подсказка «Пароль», подписи глаза, язык в шапке, «Далее» → пароль', (t) async {
      _phone(t);
      await t.pumpWidget(_app(const LoginScreen(), overrides: [databaseProvider.overrideWithValue(db)]));
      await t.pump();
      expect(t.takeException(), isNull);
      final l = lookupAppLocalizations(const Locale('ru'));
      final fields = t.widgetList<TextField>(find.byType(TextField)).toList();
      expect(fields[1].decoration!.hintText, l.passwordLabel);
      expect(find.byTooltip(l.a11yShowPassword), findsOneWidget);
      await t.tap(find.byTooltip(l.a11yShowPassword));
      await t.pump();
      expect(find.byTooltip(l.a11yHidePassword), findsOneWidget);
      expect(find.byTooltip(l.language), findsOneWidget);
      expect(t.getSize(find.byTooltip(l.language)).height, greaterThanOrEqualTo(48));
      await t.tap(find.byType(TextField).first);
      await t.pump();
      await t.testTextInput.receiveAction(TextInputAction.next);
      await t.pump();
      expect(fields[1].focusNode!.hasFocus, isTrue);
    });

    testWidgets('выбор языка переключает язык приложения', (t) async {
      _phone(t);
      late WidgetRef r;
      await t.pumpWidget(_app(Consumer(builder: (c, ref, _) {
        r = ref;
        return const LoginScreen();
      }), overrides: [databaseProvider.overrideWithValue(db)]));
      await t.pump();
      await t.tap(find.byTooltip('Язык'));
      await t.pumpAndSettle();
      await t.tap(find.text('English'));
      await t.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 100)));
      await t.pump();
      expect(r.read(prefsProvider).locale.languageCode, 'en');
    });
  });
}
