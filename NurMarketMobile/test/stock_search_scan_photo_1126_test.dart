// 2026-11-26 (владелец: «в мобилке на складе поиск товара — добавь поиск сканером и по виду»): в строке поиска «Склада» две кнопки —
// «Сканер штрихкода» и «Поиск по фото». Камера и ИИ подменены (debugScan / debugPhotoSearch), сеть и боевая база не трогаются:
// подставной API, база в памяти. Проверено: код → товар в поиске, неизвестный код, нет связи, фото → список с остатком и ценой → выбор,
// пустой ответ, отмена, кнопки ≥ 48 dp на 360 dp и ky/uz при шрифте 1,3, в списке фото нет закупки.
import 'package:drift/drift.dart' show Value;
import 'package:drift/native.dart';
import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_riverpod/misc.dart' show Override;
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/ai/vision.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/providers.dart';
import 'package:nurmarket/core/theme/theme.dart';
import 'package:nurmarket/features/scan/code_lookup.dart';
import 'package:nurmarket/features/stock/stock_screens.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

class _Fixed extends SessionController {
  final Session s;
  _Fixed(this.s);
  @override
  Session? build() => s;
}

const _cashier = Session(email: 'k@x.kg', access: 'a', refresh: 'r', userId: 'u3', userName: 'Айгуль', role: 'cashier', companyName: 'Нур', cashboxId: 'b', cashboxName: 'Касса 1');

class _Api extends ApiClient {
  _Api(this.handler) : super(SessionStore()) {
    attach(const Session(email: 'a@b.kg', access: 'a', refresh: 'r', userId: 'u1', cashboxId: 'box1', cashboxName: 'Касса'));
  }
  final Future<dynamic> Function(String m, String p) handler;
  final calls = <String>[];
  @override
  Future<dynamic> get(String path, {Map<String, dynamic>? query, Duration? timeout}) {
    calls.add('GET $path');
    return handler('GET', path);
  }
}

void _phone(WidgetTester t, {double scale = 1.0, double w = 360, double h = 740}) {
  t.view.physicalSize = Size(w * 2, h * 2);
  t.view.devicePixelRatio = 2.0;
  t.platformDispatcher.textScaleFactorTestValue = scale;
  final prev = FlutterError.onError;
  FlutterError.onError = (d) {
    if (d.exceptionAsString().contains('not supported by all of its localization delegates')) return; // ky нет в Material
    prev?.call(d);
  };
  addTearDown(() {
    FlutterError.onError = prev;
    t.view.resetPhysicalSize();
    t.view.resetDevicePixelRatio();
    t.platformDispatcher.clearTextScaleFactorTestValue();
  });
}

Widget _app(Widget home, {String lang = 'ru', List<Override> overrides = const []}) => ProviderScope(
  overrides: overrides,
  child: MaterialApp(
    theme: buildTheme(dark: false),
    locale: Locale(lang),
    supportedLocales: AppLocalizations.supportedLocales,
    localizationsDelegates: const [AppLocalizations.delegate, GlobalMaterialLocalizations.delegate, GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate],
    home: Scaffold(body: home),
  ),
);

void main() {
  late AppDatabase db;

  setUp(() async {
    CodeLookup.clearCache(gap: Duration.zero);
    db = AppDatabase(NativeDatabase.memory());
    await db.replaceProducts([
      ProductsCompanion.insert(id: 'p1', name: 'Винстон Блю', price: const Value(200), quantity: const Value(12), barcode: const Value('4700000000011'), searchKey: const Value('винстон блю')),
      ProductsCompanion.insert(id: 'p2', name: 'Вода Бишкек', price: const Value(15), quantity: const Value(0), barcode: const Value('4700000000028'), searchKey: const Value('вода бишкек')),
      ProductsCompanion.insert(id: 'p3', name: 'Молоко Ала-Тоо', price: const Value(90), quantity: const Value(30), barcode: const Value('4700000000035'), searchKey: const Value('молоко ала-тоо')),
    ]);
  });
  tearDown(() => db.close());

  Widget screen({Future<String?> Function()? scan, Future<List<SeenMatch>?> Function()? photo, _Api? api, Session? session, String lang = 'ru'}) => _app(
    StockScreen(debugScan: scan, debugPhotoSearch: photo),
    lang: lang,
    overrides: [
      databaseProvider.overrideWithValue(db),
      apiProvider.overrideWithValue(api ?? _Api((m, p) async => {'results': <dynamic>[]})),
      if (session != null) sessionProvider.overrideWith(() => _Fixed(session)),
    ],
  );

  String searchText(WidgetTester t) => t.widget<TextField>(find.byType(TextField).first).controller!.text;

  Future<Product> product(String id) async => (await db.productById(id))!;

  testWidgets('две кнопки рядом с поиском: подписи, размер ≥ 48 dp, ru/ky/uz на 360 dp при шрифте 1,3', (t) async {
    for (final lang in ['ru', 'ky', 'uz', 'en', 'tr']) {
      _phone(t, scale: 1.3);
      await t.pumpWidget(screen(lang: lang));
      await t.pumpAndSettle();
      for (final k in ['stockScanBtn', 'stockPhotoBtn']) {
        final f = find.byKey(Key(k));
        expect(f, findsOneWidget, reason: '$lang $k');
        final size = t.getSize(f);
        expect(size.width, greaterThanOrEqualTo(48), reason: '$lang $k');
        expect(size.height, greaterThanOrEqualTo(48), reason: '$lang $k');
      }
      final l = AppLocalizations.of(t.element(find.byType(StockScreen)))!;
      expect(find.byTooltip(l.stockScanBtn), findsOneWidget);
      expect(find.byTooltip(l.stockPhotoBtn), findsOneWidget);
      expect(t.takeException(), isNull, reason: lang);
    }
  });

  testWidgets('сканер: штрихкод найден — товар в поиске, остальные скрыты', (t) async {
    _phone(t);
    await t.pumpWidget(screen(scan: () async => '4700000000011'));
    await t.pumpAndSettle();
    expect(find.text('Вода Бишкек'), findsOneWidget);
    await t.tap(find.byKey(const Key('stockScanBtn')));
    await t.pumpAndSettle();
    expect(searchText(t), 'Винстон Блю');
    expect(find.text('Винстон Блю'), findsWidgets);
    expect(find.text('Вода Бишкек'), findsNothing);
    expect(find.textContaining('Найдено по коду 4700000000011'), findsOneWidget);
  });

  testWidgets('сканер: фильтр «Нет в наличии» не прячет найденный товар', (t) async {
    _phone(t);
    await t.pumpWidget(_app(
      StockScreen(filter: 'out', debugScan: () async => '4700000000035'),
      overrides: [databaseProvider.overrideWithValue(db), apiProvider.overrideWithValue(_Api((m, p) async => {'results': <dynamic>[]}))],
    ));
    await t.pumpAndSettle();
    expect(find.text('Молоко Ала-Тоо'), findsNothing); // остаток 30 — под фильтром «нет» его не видно
    await t.tap(find.byKey(const Key('stockScanBtn')));
    await t.pumpAndSettle();
    expect(find.text('Молоко Ала-Тоо'), findsWidgets);
  });

  testWidgets('сканер: неизвестный код — остаётся в поиске, «не найден»', (t) async {
    _phone(t);
    final api = _Api((m, p) async => {'results': <dynamic>[]});
    await t.pumpWidget(screen(scan: () async => '4799999999999', api: api));
    await t.pumpAndSettle();
    await t.tap(find.byKey(const Key('stockScanBtn')));
    await t.pumpAndSettle();
    expect(searchText(t), '4799999999999');
    expect(find.textContaining('4799999999999 не найден'), findsOneWidget);
    expect(api.calls.any((c) => c.contains('products/list')), isTrue); // спросил сервер, как в продаже
  });

  testWidgets('сканер: сервер не отвечает — «проверьте связь», а не «не найден»', (t) async {
    _phone(t);
    final api = _Api((m, p) async => throw ApiException('Нет связи'));
    await t.pumpWidget(screen(scan: () async => '4799999999999', api: api));
    await t.pumpAndSettle();
    await t.tap(find.byKey(const Key('stockScanBtn')));
    await t.pumpAndSettle();
    expect(find.textContaining('сервер не отвечает'), findsOneWidget);
  });

  testWidgets('сканер: закрыли без кода — список не меняется', (t) async {
    _phone(t);
    await t.pumpWidget(screen(scan: () async => null));
    await t.pumpAndSettle();
    await t.tap(find.byKey(const Key('stockScanBtn')));
    await t.pumpAndSettle();
    expect(searchText(t), '');
    expect(find.text('Вода Бишкек'), findsOneWidget);
  });

  testWidgets('фото: список подходящих (название, остаток, цена), выбор → товар в поиске; закупки нет', (t) async {
    _phone(t);
    final p1 = await product('p1'), p2 = await product('p2'), p3 = await product('p3');
    // один товар дважды (с разной оценкой) — в списке один раз, лучшая оценка выше; порядок — по оценке
    final matches = [
      SeenMatch(const SeenItem(name: 'Пачка сигарет'), [(p2, 0.6), (p1, 0.9)]),
      SeenMatch(const SeenItem(name: 'Вода'), [(p2, 0.7), (p3, 0.4)]),
    ];
    await t.pumpWidget(screen(photo: () async => matches, session: _cashier));
    await t.pumpAndSettle();
    await t.tap(find.byKey(const Key('stockPhotoBtn')));
    await t.pumpAndSettle();
    expect(find.text('Похожие товары на складе'), findsOneWidget);
    final rows = [for (final id in ['p1', 'p2', 'p3']) find.byKey(ValueKey('photoHit-$id'))];
    for (final r in rows) {
      expect(r, findsOneWidget);
      expect(t.getSize(r).height, greaterThanOrEqualTo(48));
    }
    expect(t.getTopLeft(rows[0]).dy, lessThan(t.getTopLeft(rows[1]).dy)); // 0,9 выше 0,7
    expect(t.getTopLeft(rows[1]).dy, lessThan(t.getTopLeft(rows[2]).dy));
    expect(find.descendant(of: rows[0], matching: find.text('12 шт')), findsOneWidget);
    expect(find.descendant(of: rows[0], matching: find.text('200 сом')), findsOneWidget);
    expect(find.descendant(of: rows[1], matching: find.text('0 шт')), findsOneWidget);
    expect(find.textContaining('акуп'), findsNothing);
    await t.tap(rows[2]);
    await t.pumpAndSettle();
    expect(find.text('Похожие товары на складе'), findsNothing);
    expect(searchText(t), 'Молоко Ала-Тоо');
    expect(find.text('Винстон Блю'), findsNothing);
  });

  testWidgets('фото: ничего похожего — понятное сообщение, список не меняется', (t) async {
    _phone(t);
    await t.pumpWidget(screen(photo: () async => [const SeenMatch(SeenItem(name: 'Что-то'), [])]));
    await t.pumpAndSettle();
    await t.tap(find.byKey(const Key('stockPhotoBtn')));
    await t.pumpAndSettle();
    expect(find.textContaining('ничего похожего на складе не нашлось'), findsOneWidget);
    expect(find.text('Вода Бишкек'), findsOneWidget);
  });

  testWidgets('фото: отмена (null) — ничего не происходит', (t) async {
    _phone(t);
    await t.pumpWidget(screen(photo: () async => null));
    await t.pumpAndSettle();
    await t.tap(find.byKey(const Key('stockPhotoBtn')));
    await t.pumpAndSettle();
    expect(find.text('Похожие товары на складе'), findsNothing);
    expect(searchText(t), '');
  });

  testWidgets('фото без подмены: связи нет — сообщение до съёмки, камера не открывается', (t) async {
    _phone(t);
    final api = _Api((m, p) async => {'results': <dynamic>[]});
    api.link.value = const LinkStatus(offline: true);
    await t.pumpWidget(screen(api: api));
    await t.pumpAndSettle();
    await t.tap(find.byKey(const Key('stockPhotoBtn')));
    await t.pumpAndSettle();
    expect(find.textContaining('поиск по фото работает только с интернетом'), findsOneWidget);
  });
}
