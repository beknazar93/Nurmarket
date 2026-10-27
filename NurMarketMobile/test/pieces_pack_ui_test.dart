// 2026-10-27 (М4): экраны поштучной продажи из упаковки — блок в карточке товара (включение, шаблоны, цена штуки «сама»,
// проверки, packages_input), выбор «Пачка / Штука» в продаже и подписи строк чека, остаток «12 уп. + 5 шт» на плитке и в складе;
// 360 dp, ky/uz, шрифт 1,3, светлая и тёмная. Сервер и сеть не трогаются: подставной API, база в памяти.
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
import 'package:nurmarket/core/providers.dart';
import 'package:nurmarket/core/theme/theme.dart';
import 'package:nurmarket/core/util/audit.dart';
import 'package:nurmarket/features/sale/sale_screen.dart';
import 'package:nurmarket/features/shift/shift_repository.dart';
import 'package:nurmarket/features/stock/product_edit.dart';
import 'package:nurmarket/features/stock/product_edit_repository.dart';
import 'package:nurmarket/features/stock/product_packages.dart';
import 'package:nurmarket/features/stock/stock_repository.dart';
import 'package:nurmarket/features/stock/stock_screens.dart';
import 'package:nurmarket/features/admin/new_item.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

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

  Map? get lastPatch {
    final i = calls.lastIndexWhere((c) => c.startsWith('PATCH'));
    return i < 0 ? null : bodies[i] as Map;
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

Future<void> _fonts() async {
  final m = FontLoader('Montserrat');
  for (final w in ['Regular', 'Medium', 'SemiBold', 'Bold', 'ExtraBold']) {
    m.addFont(rootBundle.load('assets/fonts/Montserrat-$w.ttf'));
  }
  await m.load();
}

/// Тексты, обрезанные по maxLines, внутри [root] (по умолчанию весь экран).
List<String> _clipped(WidgetTester t, [Finder? root]) {
  final out = <String>[];
  final all = root == null ? find.byType(RichText) : find.descendant(of: root, matching: find.byType(RichText));
  for (final e in all.evaluate()) {
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

Map<String, dynamic> _serverPack({num qty = 20, String price = '12.00'}) => {'id': 'pk1', 'name': 'Пачка', 'quantity_in_package': qty, 'unit': 'шт', 'piece_unit_price': price};

final _product = Product(
  id: 'p1',
  name: 'Винстон',
  price: 200,
  wholesalePrice: 0,
  barcode: '4700000000011',
  altBarcodes: '',
  unit: 'шт',
  isWeight: false,
  isService: false,
  isFavorite: false,
  quantity: 12.25,
  category: '',
  searchKey: 'винстон',
  promoRules: '',
  minQuantity: 0,
);

void main() {
  setUpAll(() async {
    TestWidgetsFlutterBinding.ensureInitialized();
    Audit.testDirectory = Directory.systemTemp.createTempSync('nm_packui_');
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger.setMockMethodCallHandler(SystemChannels.platform, (c) async => null);
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger.setMockMethodCallHandler(const MethodChannel('kg.nurcrm.nurmarket/beep'), (c) async => null);
    await _fonts();
  });

  // ------------------------------------------------------------------ карточка товара
  group('карточка товара: блок «Упаковки / поштучная продажа»', () {
    late AppDatabase db;
    late _Api api;
    late Map<String, dynamic> server;

    setUp(() {
      db = AppDatabase(NativeDatabase.memory());
      server = {
        'id': 'p1',
        'name': 'Винстон',
        'price': '300.00',
        'purchase_price': '200.00',
        'markup_percent': '50',
        'wholesale_price': '0',
        'quantity': '12.25',
        'unit': 'шт',
        'barcode': '4700000000011',
        'alternate_barcodes': [],
        'packages': <dynamic>[],
      };
      api = _Api((m, p, b) async {
        if (m == 'GET' && p == 'api/main/products/p1/') return Map<String, dynamic>.from(server);
        if (m == 'PATCH' && p == 'api/main/products/p1/') {
          final body = (b as Map).cast<String, dynamic>();
          final inp = body['packages_input'];
          server.addAll({
            for (final e in body.entries)
              if (e.key != 'packages_input') e.key: e.value,
          });
          if (inp is List) {
            server['packages'] = [
              for (final x in inp.cast<Map>()) {'id': 'pk1', 'name': x['name'], 'quantity_in_package': x['quantity_in_package'], 'unit': x['unit'], 'piece_unit_price': x['piece_unit_price']},
            ];
          }
          return Map<String, dynamic>.from(server);
        }
        throw ApiException('нет пути $m $p', status: 404);
      });
    });
    tearDown(() => db.close());

    Future<void> open(WidgetTester t, {String lang = 'ru', bool dark = false}) async {
      await t.pumpWidget(
        _app(
          ProductEditScreen(product: _product),
          lang: lang,
          dark: dark,
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

    Future<void> toTop(WidgetTester t) async {
      await t.drag(find.byType(Scrollable).first, const Offset(0, 5000));
      await t.pumpAndSettle();
    }

    Future<void> showPack(WidgetTester t) async {
      await toTop(t);
      await t.scrollUntilVisible(find.byKey(const Key('packSwitch')), 300, scrollable: find.byType(Scrollable).first);
      await t.pumpAndSettle();
    }

    /// Поле «Продажная цена» (второе поле сверху) — после прокрутки к началу списка.
    Future<Finder> priceField(WidgetTester t) async {
      await toTop(t);
      return find.byType(TextField).at(1);
    }

    Future<void> tapSave(WidgetTester t) async {
      final save = find.widgetWithText(FilledButton, 'Сохранить');
      await t.scrollUntilVisible(save, 300, scrollable: find.byType(Scrollable).first);
      await t.pumpAndSettle();
      await t.tap(save);
      await t.pumpAndSettle();
    }

    String text(WidgetTester t, String key) => t.widget<TextField>(find.byKey(Key(key))).controller!.text;

    testWidgets('включение, шаблон «Пачка 20», цена штуки сама; своя цена не затирается; «Авто» возвращает', (t) async {
      _phone(t);
      await open(t);
      await showPack(t);
      expect(find.byKey(const Key('packQty')), findsNothing, reason: 'выключено — полей нет');
      await t.tap(find.byKey(const Key('packSwitch')));
      await t.pumpAndSettle();
      await t.tap(find.byKey(const Key('packTpl_20_Пачка')));
      await t.pumpAndSettle();
      expect(text(t, 'packName'), 'Пачка');
      expect(text(t, 'packQty'), '20');
      expect(text(t, 'packPiece'), '15', reason: '300 ÷ 20');
      expect(find.textContaining('Наценка штуки: 50 %'), findsOneWidget, reason: 'закупка штуки 200 ÷ 20 = 10 → 15 это +50 %');
      // цена упаковки изменилась — цена штуки «сама» следом
      await t.enterText(await priceField(t), '400');
      await t.pump();
      await showPack(t);
      expect(text(t, 'packPiece'), '20');
      // правим руками — больше не пересчитывается
      await t.enterText(find.byKey(const Key('packPiece')), '22');
      await t.pump();
      await t.enterText(await priceField(t), '500');
      await t.pump();
      await showPack(t);
      expect(text(t, 'packPiece'), '22');
      await t.tap(find.byKey(const Key('packAuto')));
      await t.pump();
      expect(text(t, 'packPiece'), '25');
    });

    testWidgets('сохранение: один PATCH с packages_input как у кассы; упаковка с id попадает в базу телефона', (t) async {
      _phone(t);
      await open(t);
      await showPack(t);
      await t.tap(find.byKey(const Key('packSwitch')));
      await t.pumpAndSettle();
      await t.tap(find.byKey(const Key('packTpl_20_Пачка')));
      await t.pumpAndSettle();
      await tapSave(t);
      expect(api.calls.where((c) => c.startsWith('PATCH')).length, 1);
      expect(api.lastPatch!['packages_input'], [
        {'name': 'Пачка', 'quantity_in_package': 20, 'unit': 'шт', 'piece_unit_price': '15.00'},
      ]);
      expect(api.lastPatch!.keys, ['packages_input'], reason: 'цены не менялись — в запросе только упаковка');
      await t.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 100)));
      final saved = (await t.runAsync(() => PackageStore.read(db)))!['p1']!;
      expect(saved.id, 'pk1');
    });

    testWidgets('штук 1 / дробное — ошибка под полем, на сервер ничего не ушло; штук 0 — то же', (t) async {
      _phone(t);
      await open(t);
      await showPack(t);
      await t.tap(find.byKey(const Key('packSwitch')));
      await t.pumpAndSettle();
      await t.enterText(find.byKey(const Key('packQty')), '1');
      await t.enterText(find.byKey(const Key('packPiece')), '10');
      await t.pump();
      expect(find.byKey(const Key('packError')), findsOneWidget);
      expect(find.text('Штук в упаковке — целое число, не меньше 2'), findsOneWidget);
      await tapSave(t);
      expect(api.calls.where((c) => c.startsWith('PATCH')), isEmpty);
      await showPack(t);
      await t.enterText(find.byKey(const Key('packQty')), '2.5');
      await t.pump();
      expect(find.byKey(const Key('packError')), findsOneWidget);
    });

    testWidgets('цена штуки × штук расходится с ценой упаковки больше чем на 5 %: вопрос; «Исправить» — ничего не ушло, «Сохранить как есть» — ушло', (t) async {
      _phone(t);
      await open(t);
      await showPack(t);
      await t.tap(find.byKey(const Key('packSwitch')));
      await t.pumpAndSettle();
      await t.tap(find.byKey(const Key('packTpl_20_Пачка')));
      await t.pumpAndSettle();
      await t.enterText(find.byKey(const Key('packPiece')), '14'); // 280 против 300 — 6,7 %
      await t.pump();
      expect(find.byKey(const Key('packWarn')), findsOneWidget);
      await tapSave(t);
      expect(find.byKey(const Key('packMismatchDialog')), findsOneWidget);
      await t.tap(find.byKey(const Key('packMismatchFix')));
      await t.pumpAndSettle();
      expect(api.calls.where((c) => c.startsWith('PATCH')), isEmpty);
      await tapSave(t);
      await t.tap(find.byKey(const Key('packMismatchKeep')));
      await t.pumpAndSettle();
      expect(api.calls.where((c) => c.startsWith('PATCH')).length, 1);
      expect(((api.lastPatch!['packages_input'] as List).single as Map)['piece_unit_price'], '14.00');
    });

    testWidgets('«кг» — весовой: переключатель выключается и недоступен, packages_input не уходит', (t) async {
      _phone(t);
      await open(t);
      await showPack(t);
      await t.tap(find.byKey(const Key('packSwitch')));
      await t.pumpAndSettle();
      expect(find.byKey(const Key('packQty')), findsOneWidget);
      await toTop(t);
      await t.scrollUntilVisible(find.widgetWithText(ChoiceChip, 'кг'), 300, scrollable: find.byType(Scrollable).first);
      await t.tap(find.widgetWithText(ChoiceChip, 'кг'));
      await t.pumpAndSettle();
      await showPack(t);
      expect(find.byKey(const Key('packQty')), findsNothing, reason: 'весовой нельзя продавать поштучно');
      expect(t.widget<SwitchListTile>(find.byKey(const Key('packSwitch'))).onChanged, isNull);
      expect(find.text('Весовой товар (кг) нельзя продавать поштучно из упаковки.'), findsOneWidget);
    });

    testWidgets('у товара уже есть упаковка: форма заполнена с сервера, остаток «12 уп. + 5 шт», выключили — packages_input пуст', (t) async {
      _phone(t);
      server['packages'] = [_serverPack()];
      await open(t);
      await t.scrollUntilVisible(find.text('12 уп. + 5 шт'), 300, scrollable: find.byType(Scrollable).first);
      expect(find.text('12 уп. + 5 шт'), findsOneWidget);
      await showPack(t);
      expect(text(t, 'packQty'), '20');
      expect(text(t, 'packPiece'), '12');
      expect(find.textContaining('Цена упаковки: 300'), findsOneWidget);
      await t.tap(find.byKey(const Key('packSwitch')));
      await t.pumpAndSettle();
      await tapSave(t);
      expect(api.lastPatch!['packages_input'], isEmpty);
    });

    testWidgets('карточка сервера без поля packages — упаковка берётся из каталога телефона', (t) async {
      _phone(t);
      server.remove('packages');
      await PackageStore.put(db, 'p1', const PiecePack(id: 'pk1', name: 'Пачка', qty: 20, piecePrice: 15));
      await open(t);
      await showPack(t);
      expect(text(t, 'packQty'), '20');
      expect(text(t, 'packPiece'), '15');
    });

    testWidgets('несколько упаковок у товара — блок только читается, packages_input не уходит', (t) async {
      _phone(t);
      server['packages'] = [
        _serverPack(),
        {..._serverPack(qty: 200, price: '1.00'), 'id': 'pk2', 'name': 'Блок'},
      ];
      await open(t);
      await t.scrollUntilVisible(find.text('У товара несколько упаковок — их правят на сайте или кассе.'), 300, scrollable: find.byType(Scrollable).first);
      expect(find.byKey(const Key('packSwitch')), findsNothing);
    });

    for (final lang in ['ru', 'ky', 'uz']) {
      for (final dark in [false, true]) {
        testWidgets('360 dp, шрифт 1,3, $lang${dark ? ', тёмная' : ''}: блок упаковок без переполнений, цели ≥ 48 dp', (t) async {
          _phone(t, scale: 1.3);
          server['packages'] = [_serverPack()];
          await open(t, lang: lang, dark: dark);
          await showPack(t);
          expect(t.getSize(find.byKey(const Key('packSwitch'))).height, greaterThanOrEqualTo(48));
          expect(t.getSize(find.byKey(Key('packTpl_20_${{'ru': 'Пачка', 'ky': 'Пачка', 'uz': 'Pachka'}[lang]}'))).height, greaterThanOrEqualTo(48));
          expect(t.getSize(find.byKey(const Key('packAuto'))).height, greaterThanOrEqualTo(48));
          expect(t.takeException(), isNull);
          expect(_clipped(t, find.byKey(const Key('packSection'))), isEmpty);
        });
      }
    }
  });

  // ------------------------------------------------------------------ продажа
  group('продажа: «Пачка / Штука»', () {
    late AppDatabase db;
    late ProviderContainer container;

    setUp(() async {
      db = AppDatabase(NativeDatabase.memory());
      await db.replaceProducts([
        ProductsCompanion.insert(id: 'p1', name: 'Винстон', price: const Value(200), quantity: const Value(12.25), searchKey: const Value('винстон')),
        ProductsCompanion.insert(id: 'p2', name: 'Вода', price: const Value(15), quantity: const Value(30), searchKey: const Value('вода')),
      ]);
      await PackageStore.put(db, 'p1', const PiecePack(id: 'pk1', name: 'Пачка', qty: 20, piecePrice: 12));
    });
    tearDown(() => db.close());

    Future<void> open(WidgetTester t, {String lang = 'ru', bool dark = false}) async {
      await t.pumpWidget(
        _app(
          const SaleScreen(),
          lang: lang,
          dark: dark,
          overrides: [
            databaseProvider.overrideWithValue(db),
            apiProvider.overrideWithValue(_Api((m, p, b) async => {'results': <dynamic>[]})),
            sessionProvider.overrideWith(() => _Fixed(_owner)),
            shiftProvider.overrideWith(_OpenShift.new),
          ],
        ),
      );
      await t.pumpAndSettle();
      container = ProviderScope.containerOf(t.element(find.byType(SaleScreen)));
    }

    testWidgets('плитка показывает остаток «12 уп. + 5 шт»; обычный товар — «30 шт» как раньше', (t) async {
      _phone(t);
      await open(t);
      expect(find.text('12 уп. + 5 шт'), findsOneWidget);
      expect(find.text('30 шт'), findsOneWidget);
    });

    testWidgets('нажали на товар с упаковкой → две кнопки с ценами; «Штука» — строка с sale_package_id по цене штуки, «Пачка» — обычная строка', (t) async {
      _phone(t);
      await open(t);
      await t.tap(find.text('Винстон'));
      await t.pumpAndSettle();
      expect(find.byKey(const Key('packChoiceSheet')), findsOneWidget);
      expect(t.getSize(find.byKey(const Key('packChoiceWhole'))).height, greaterThanOrEqualTo(48));
      expect(t.getSize(find.byKey(const Key('packChoicePiece'))).height, greaterThanOrEqualTo(48));
      expect(find.textContaining('200'), findsWidgets);
      expect(find.textContaining('12'), findsWidgets);
      await t.tap(find.byKey(const Key('packChoicePiece')));
      await t.pumpAndSettle();
      var lines = container.read(cartProvider).lines;
      expect(lines.length, 1);
      expect(lines.single.salePackageId, 'pk1');
      expect(lines.single.price, 12);
      expect(lines.single.qty, 1);
      expect(lines.single.perPack, 20);
      // ещё раз — выбираем целую пачку
      await t.tap(find.text('Винстон'));
      await t.pumpAndSettle();
      await t.tap(find.byKey(const Key('packChoiceWhole')));
      await t.pumpAndSettle();
      lines = container.read(cartProvider).lines;
      expect(lines.length, 2, reason: 'штука и пачка — разные строки');
      expect(lines[1].salePackageId, isNull);
      expect(lines[1].price, 200);
      expect(lines[1].packName, 'Пачка');
      expect(container.read(cartProvider).total, 212);
      // ещё штука — прибавляется к строке штук
      await t.tap(find.text('Винстон'));
      await t.pumpAndSettle();
      await t.tap(find.byKey(const Key('packChoicePiece')));
      await t.pumpAndSettle();
      expect(container.read(cartProvider).lines.length, 2);
      expect(container.read(cartProvider).lines[0].qty, 2);
      // вкладка «Чек»: подписи строк
      await t.tap(find.byIcon(Icons.receipt_long_rounded));
      await t.pumpAndSettle();
      expect(find.byKey(const Key('packTag_piece')), findsOneWidget);
      expect(find.text('шт. из «Пачка»'), findsOneWidget);
      expect(find.byKey(const Key('packTag_whole')), findsOneWidget);
    });

    testWidgets('закрыли выбор — ничего не добавлено; товар без упаковки добавляется сразу без вопроса', (t) async {
      _phone(t);
      await open(t);
      await t.tap(find.text('Винстон'));
      await t.pumpAndSettle();
      await t.tapAt(const Offset(180, 40)); // за пределами листа
      await t.pumpAndSettle();
      expect(container.read(cartProvider).lines, isEmpty);
      await t.tap(find.text('Вода'));
      await t.pumpAndSettle();
      expect(find.byKey(const Key('packChoiceSheet')), findsNothing);
      expect(container.read(cartProvider).lines.single.salePackageId, isNull);
      expect(container.read(cartProvider).lines.single.packName, isNull);
    });

    testWidgets('упаковка без id на сервере (ещё не пришла в каталог) — продаётся целиком, без вопроса', (t) async {
      _phone(t);
      await PackageStore.put(db, 'p1', const PiecePack(name: 'Пачка', qty: 20, piecePrice: 12));
      await open(t);
      await t.tap(find.text('Винстон'));
      await t.pumpAndSettle();
      expect(find.byKey(const Key('packChoiceSheet')), findsNothing);
      expect(container.read(cartProvider).lines.single.salePackageId, isNull);
    });

    testWidgets('в чеке штуки целые: «+» прибавляет 1, ввод 2,6 → 3', (t) async {
      _phone(t);
      await open(t);
      container
          .read(cartProvider.notifier)
          .add(
            (await t.runAsync<Product?>(() => db.productById('p1')))!,
            piece: const PiecePack(id: 'pk1', name: 'Пачка', qty: 20, piecePrice: 12),
            packName: 'Пачка',
          );
      await t.pump();
      container.read(cartProvider.notifier).setQty(0, 7);
      expect(container.read(cartProvider).lines.single.stockQty, 0.35);
      expect(container.read(cartProvider).total, 84);
    });

    for (final lang in ['ru', 'ky', 'uz']) {
      for (final dark in [false, true]) {
        testWidgets('360 dp, шрифт 1,3, $lang${dark ? ', тёмная' : ''}: выбор «Пачка / Штука» и строки чека без переполнений', (t) async {
          _phone(t, scale: 1.3);
          await open(t, lang: lang, dark: dark);
          await t.tap(find.text('Винстон'));
          await t.pumpAndSettle();
          expect(t.takeException(), isNull);
          expect(_clipped(t, find.byKey(const Key('packChoiceSheet'))), isEmpty);
          await t.tap(find.byKey(const Key('packChoicePiece')));
          await t.pumpAndSettle();
          await t.tap(find.byIcon(Icons.receipt_long_rounded));
          await t.pumpAndSettle();
          expect(find.byKey(const Key('packTag_piece')), findsOneWidget);
          expect(t.takeException(), isNull);
          expect(_clipped(t), isEmpty);
        });
      }
    }
  });
  // ------------------------------------------------------------------ склад и новый товар
  group('склад и новый товар', () {
    late AppDatabase db;
    setUp(() async {
      db = AppDatabase(NativeDatabase.memory());
      await db.replaceProducts([
        ProductsCompanion.insert(id: 'p1', name: 'Винстон', price: const Value(200), quantity: const Value(12.25), searchKey: const Value('винстон')),
        ProductsCompanion.insert(id: 'p2', name: 'Вода', price: const Value(15), quantity: const Value(30), searchKey: const Value('вода')),
      ]);
      await PackageStore.put(db, 'p1', const PiecePack(id: 'pk1', name: 'Пачка', qty: 20, piecePrice: 12));
    });
    tearDown(() => db.close());

    testWidgets('список склада: «12 уп. + 5 шт» у товара с упаковкой, «30 шт» у обычного', (t) async {
      _phone(t);
      await t.pumpWidget(
        _app(
          const StockScreen(),
          overrides: [
            databaseProvider.overrideWithValue(db),
            apiProvider.overrideWithValue(_Api((m, p, b) async => {'results': <dynamic>[]})),
            sessionProvider.overrideWith(() => _Fixed(_owner)),
          ],
        ),
      );
      await t.pumpAndSettle();
      expect(find.text('12 уп. + 5 шт'), findsOneWidget);
      expect(find.text('30 шт'), findsOneWidget);
    });

    testWidgets('новый товар: включили упаковку «Пачка 20» → создание с packages_input как у кассы', (t) async {
      _phone(t, h: 900);
      final created = <Map>[];
      final api = _Api((m, p, b) async {
        if (m == 'POST' && p == 'api/main/products/create-manual/') {
          created.add((b as Map).cast<String, dynamic>());
          return {'id': 'n${created.length}'};
        }
        if (m == 'GET' && p.contains('suppliers')) return <dynamic>[];
        if (m == 'GET' && p.contains('products/list')) return {'results': <dynamic>[]};
        return <String, dynamic>{};
      });
      await t.pumpWidget(_app(const NewItemScreen(), overrides: [databaseProvider.overrideWithValue(db), apiProvider.overrideWithValue(api), sessionProvider.overrideWith(() => _Fixed(_owner))]));
      await t.pumpAndSettle();
      Future<void> scrollTo(Finder f) async {
        await t.scrollUntilVisible(f, 300, scrollable: find.byType(Scrollable).first);
        await t.pumpAndSettle();
      }

      // название — первое поле после штрихкода; цены — по подписям
      final fields = find.byType(TextField);
      await t.enterText(fields.at(1), 'Сигареты Тест'); // 0 — штрихкод, 1 — название
      await t.enterText(fields.at(4), '150'); // закупка
      await t.enterText(fields.at(5), '200'); // розница
      await t.pump();
      await scrollTo(find.byKey(const Key('packSwitch')));
      await t.tap(find.byKey(const Key('packSwitch')));
      await t.pumpAndSettle();
      expect(find.text('Количество вводится в упаковках: 12 — это 12 пачек, штуки продаются из них долями.'), findsOneWidget);
      await t.tap(find.byKey(const Key('packTpl_20_Пачка')));
      await t.pumpAndSettle();
      final save = find.widgetWithText(FilledButton, 'Сохранить');
      await scrollTo(save);
      await t.tap(save);
      await t.pumpAndSettle();
      expect(created.length, 1);
      expect(created.single['packages_input'], [
        {'name': 'Пачка', 'quantity_in_package': 20, 'unit': 'шт', 'piece_unit_price': '10.00'},
      ]);
      expect(created.single['name'], 'Сигареты Тест');
    });
  });
}
