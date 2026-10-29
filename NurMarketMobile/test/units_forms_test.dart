// 2026-10-29: единицы измерения, цена и количество в «Новом товаре», карточке товара и массовой загрузке (владелец: «в добавлении
// товара нет цены, количества и единицы — шт, кг, литр»). Подставной API и база в памяти — боевой сервер не трогается.
import 'dart:io';

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
import 'package:nurmarket/core/catalog/global_products.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/providers.dart';
import 'package:nurmarket/core/theme/theme.dart';
import 'package:nurmarket/core/units.dart';
import 'package:nurmarket/core/util/audit.dart';
import 'package:nurmarket/core/util/ui.dart';
import 'package:nurmarket/features/admin/admin_receive.dart';
import 'package:nurmarket/features/admin/new_item.dart';
import 'package:nurmarket/features/stock/bulk_import.dart';
import 'package:nurmarket/features/stock/product_edit.dart';
import 'package:nurmarket/features/stock/product_edit_repository.dart';
import 'package:nurmarket/features/stock/stock_repository.dart';
import 'package:nurmarket/features/stock/unit_picker.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

class _Fixed extends SessionController {
  final Session s;
  _Fixed(this.s);
  @override
  Session? build() => s;
}

const _owner = Session(email: 'o@x.kg', access: 'a', refresh: 'r', userId: 'u2', userName: 'Бакыт О.', role: 'owner', isOwner: true, companyName: 'Нур', cashboxId: 'b', cashboxName: 'Касса 1');

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

  List<Map> bodiesOf(String method, String pathPart) => [
    for (var i = 0; i < calls.length; i++)
      if (calls[i].startsWith(method) && calls[i].contains(pathPart) && bodies[i] is Map) bodies[i] as Map,
  ];
}

/// Справочник NurCRM для теста: одна запись, без диска и сети.
class _Catalog extends GlobalProducts {
  _Catalog() : super(openFile: () async => throw StateError('нет диска'));
  @override
  Future<GlobalProduct?> local(String raw) async =>
      raw == '4600123456789' ? const GlobalProduct(barcode: '4600123456789', name: 'Кофе из справочника', brand: 'Jardin', category: 'Кофе') : null;
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

/// Тексты, обрезанные по maxLines, внутри [root] (по умолчанию весь экран, включая шапку).
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

Future<void> _scrollTo(WidgetTester t, Finder f) async {
  // сначала к началу списка: scrollUntilVisible листает только вниз
  await t.drag(find.byType(Scrollable).first, const Offset(0, 6000));
  await t.pumpAndSettle();
  await t.scrollUntilVisible(f, 300, scrollable: find.byType(Scrollable).first);
  await t.pumpAndSettle();
}

void main() {
  setUpAll(() async {
    TestWidgetsFlutterBinding.ensureInitialized();
    Audit.testDirectory = Directory.systemTemp.createTempSync('nm_units_');
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger.setMockMethodCallHandler(SystemChannels.platform, (c) async => null);
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger.setMockMethodCallHandler(const MethodChannel('kg.nurcrm.nurmarket/beep'), (c) async => null);
    await _fonts();
  });

  // ------------------------------------------------------------------ каталог единиц и числа
  group('единицы и числа', () {
    test('единицы на 5 языках приводятся к одному коду', () {
      const cases = {
        'шт': ['Шт', 'штука', 'шт.', 'pcs', 'PC', 'дана', 'даана', 'dona', 'adet', 'sht'],
        'кг': ['кг', 'Кг.', 'килограмм', 'kg', 'Kilogram', 'kilogramm'],
        'г': ['г', 'гр', 'грамм', 'gram'],
        'л': ['л', 'литр', 'Л.', 'l', 'lt', 'litr', 'litre', 'liter'],
        'мл': ['мл', 'ml', 'миллилитр'],
        'м': ['м', 'метр', 'm', 'metr', 'metre'],
        'упак': ['упак', 'упаковка', 'пачка', 'pack', 'qadoq', 'paket', 'таңгак'],
        'пара': ['пара', 'pair', 'juft', 'çift', 'жуп'],
        'компл': ['компл', 'комплект', 'набор', 'set', 'toplam'],
      };
      for (final e in cases.entries) {
        for (final raw in e.value) {
          expect(normalizeUnit(raw), e.key, reason: raw);
        }
      }
      expect(normalizeUnit(''), 'шт');
      expect(normalizeUnit(null), 'шт');
      expect(normalizeUnit('рулон'), 'рулон', reason: 'своя единица остаётся как ввели');
      expect(unitOf('xyz'), isNull);
    });

    test('дробное количество: кг, г, л, мл, м — можно; шт, упак, пара — только целое; своя — можно', () {
      for (final u in ['кг', 'г', 'л', 'мл', 'м', 'рулон']) {
        expect(qtyProblem(u, 0.5), isNull, reason: u);
        expect(qtyProblem(u, 12.75), isNull, reason: u);
      }
      for (final u in ['шт', 'упак', 'пара', 'компл']) {
        expect(qtyProblem(u, 2), isNull, reason: u);
        expect(qtyProblem(u, 2.5), QtyProblem.notWhole, reason: u);
      }
      expect(qtyProblem('шт', -1), QtyProblem.negative);
      expect(qtyProblem('шт', 12.25, packed: true), isNull, reason: 'товар с упаковкой считают в упаковках, остаток дробный');
    });

    test('весовые единицы каталога совпадают с isWeightUnit карточки товара', () {
      for (final u in kUnits) {
        expect(isWeightUnit(u.code), u.weight, reason: u.code);
      }
    });

    test('parseNum: 12,75 · 12.75 · 0,5 · 1 250,5 · 1.250,50 · 1,250.50', () {
      expect(parseNum('12,75'), 12.75);
      expect(parseNum('12.75'), 12.75);
      expect(parseNum('0,5'), 0.5);
      expect(parseNum('1 250,5'), 1250.5);
      expect(parseNum('1 250,5'), 1250.5);
      expect(parseNum('1.250,50'), 1250.5);
      expect(parseNum('1,250.50'), 1250.5);
      expect(parseNum(''), isNull);
      expect(parseNum('abc'), isNull);
    });
  });

  // ------------------------------------------------------------------ разбор файла
  group('массовая загрузка: разбор Excel/CSV', () {
    test('ru: «Ед. изм.», «Цена», «Количество»; штука/литр/кг/pcs → код; дробные 12,75 и 0,5', () {
      final rows = BulkParser.parse(
        BulkParser.readText('Название;Штрихкод;Цена;Закупка;Количество;Ед. изм.\nМолоко;4870001;12,75;9,5;0,5;литр\nЯблоки;;120;90;2,5;Кг\nВода;;15;10;24;штука\nРучка;;5;3;100;pcs'),
      );
      expect(rows.map((r) => r.unit), ['л', 'кг', 'шт', 'шт']);
      expect(rows[0].price, 12.75);
      expect(rows[0].cost, 9.5);
      expect(rows[0].qty, 0.5);
      expect(rows[1].qty, 2.5);
      expect(rows.every((r) => r.valid), isTrue);
      expect(rows.every((r) => r.unitOk), isTrue);
    });

    test('ky: «Аталышы · Баасы · Саны · Бирдик»', () {
      final rows = BulkParser.parse(BulkParser.readText('Аталышы;Баасы;Саны;Бирдик\nСүт;85,5;3;литр\nНан;30;10;дана'));
      expect(rows.map((r) => r.name), ['Сүт', 'Нан']);
      expect(rows[0].price, 85.5);
      expect(rows.map((r) => r.unit), ['л', 'шт']);
      expect(rows[0].qty, 3);
    });

    test('uz и tr: «Nomi · Narx · Miqdor · Birlik», «Ürün adı · Fiyat · Miktar · Birim»', () {
      final uz = BulkParser.parse(BulkParser.readText('Nomi;Narx;Miqdor;Birlik\nSut;12000;2,5;litr'));
      expect(uz.single.price, 12000);
      expect(uz.single.qty, 2.5);
      expect(uz.single.unit, 'л');
      final tr = BulkParser.parse(BulkParser.readText('Ürün adı;Fiyat;Miktar;Birim\nSüt;90;4;adet'));
      expect(tr.single.name, 'Süt');
      expect(tr.single.unit, 'шт');
      final en = BulkParser.parse(BulkParser.readText('Name,Price,Quantity,Unit\nTape,3.5,12.5,m'));
      expect(en.single.price, 3.5);
      expect(en.single.qty, 12.5);
      expect(en.single.unit, 'м');
    });

    test('«Цена закупки» не становится «Ценой»; бренд читается', () {
      final rows = BulkParser.parse(BulkParser.readText('Название;Цена закупки;Цена продажи;Бренд\nЧай;80;110;Ahmad'));
      expect(rows.single.cost, 80);
      expect(rows.single.price, 110);
      expect(rows.single.brand, 'Ahmad');
    });

    test('ошибки строки: нет цены, незнакомая единица, дробное «шт», минус; нет количества — не ошибка', () {
      final rows = BulkParser.parse(BulkParser.readText('Название;Цена;Количество;Единица\nА;;1;шт\nБ;10;1;xyz\nВ;10;2,5;шт\nГ;10;-1;кг\nД;10;;шт'));
      expect(rows[0].problems, [RowProblem.noPrice]);
      expect(rows[1].problems, [RowProblem.badUnit]);
      expect(rows[1].unit, 'xyz');
      expect(rows[2].problems, [RowProblem.qtyWhole]);
      expect(rows[3].problems, [RowProblem.qtyNegative]);
      expect(rows[4].valid, isTrue);
      expect(rows[4].noQty, isTrue);
      expect(rows[1].copyWith(unit: 'кг', unitOk: true).valid, isTrue);
    });

    test('справочник NurCRM дополняет название, категорию и бренд, введённое не затирается', () async {
      final rows = BulkParser.parse(BulkParser.readText('Штрихкод;Цена;Количество;Категория;Бренд\n4600123456789;250;3;;\n4600123456789;250;3;Свой;Мой'));
      final (out, filled) = await ImportRow.fillNames(rows, _Catalog());
      expect(filled, 2);
      expect(out[0].name, 'Кофе из справочника');
      expect(out[0].category, 'Кофе');
      expect(out[0].brand, 'Jardin');
      expect(out[1].category, 'Свой');
      expect(out[1].brand, 'Мой');
    });
  });

  // ------------------------------------------------------------------ отправка
  group('массовая загрузка: что уходит на сервер', () {
    late AppDatabase db;
    late _Api api;
    var seq = 0;

    setUp(() {
      seq = 0;
      db = AppDatabase(NativeDatabase.memory());
      api = _Api((m, p, b) async {
        if (m == 'POST' && p == 'api/main/products/create-manual/') return {'id': 'n${++seq}'};
        if (m == 'GET' && p == 'api/main/products/list/') return {'count': 2, 'results': <dynamic>[]};
        if (m == 'GET' && p.startsWith('api/main/products/n')) return {'id': p.split('/')[3], 'quantity': '0'};
        return <String, dynamic>{};
      });
    });
    tearDown(() => db.close());

    test('кг — весовой с PLU, л и шт — нет; цена, закупка и дробное количество доходят', () async {
      final rows = BulkParser.parse(BulkParser.readText('Название;Цена;Закупка;Количество;Единица;Бренд\nЯблоки;120,5;90;12,5;кг;Сад\nМолоко;85;70;1,5;л;\nВода;15;10;24;шт;'));
      final up = BulkUploader(AdminReceiveRepository(api, db), api);
      await up.prepare(db, rows);
      final errors = await up.upload(rows);
      expect(errors, isEmpty);
      final created = api.bodiesOf('POST', 'create-manual');
      expect(created.map((b) => b['unit']), ['кг', 'л', 'шт']);
      expect(created.map((b) => b['is_weight']), [true, false, false]);
      expect(created[0]['plu'], isNotNull);
      expect(created[1].containsKey('plu'), isFalse);
      expect(created[0]['price'], '120.50');
      expect(created[0]['purchase_price'], '90.00');
      expect(created[0]['brand_name'], 'Сад');
      final patches = api.bodiesOf('PATCH', 'api/main/products/n');
      expect(patches.map((b) => b['quantity']), [12.5, 1.5, 24]);
      expect(up.uploaded.length, 3);
    });

    test('строка с ошибкой не уходит; после правки уходит; исправленная и загруженная не задваиваются', () async {
      final rows = BulkParser.parse(BulkParser.readText('Название;Цена;Количество;Единица\nХорошая;10;1;шт\nБез цены;;1;шт\nДробная;10;2,5;шт'));
      final up = BulkUploader(AdminReceiveRepository(api, db), api);
      await up.prepare(db, rows);
      expect(rows.where(up.canUpload).length, 1);
      await up.upload(rows);
      expect(api.bodiesOf('POST', 'create-manual').length, 1);
      // «Установить единицу кг» для дробной: теперь корректна, прежняя загруженная не создаётся второй раз
      final fixed = rows[2].copyWith(unit: 'кг', unitOk: true);
      final next = [rows[0], rows[1], fixed];
      up.replace(rows[2], fixed);
      await up.prepare(db, next, keep: true);
      await up.upload(next);
      expect(api.bodiesOf('POST', 'create-manual').length, 2, reason: 'только исправленная, «Хорошая» не повторяется');
      expect(api.bodiesOf('POST', 'create-manual').last['unit'], 'кг');
    });
  });

  // ------------------------------------------------------------------ экраны
  group('Новый товар: цена, количество, единица', () {
    late AppDatabase db;
    late _Api api;
    late List<Map> created;

    setUp(() {
      db = AppDatabase(NativeDatabase.memory());
      created = [];
      api = _Api((m, p, b) async {
        if (m == 'POST' && p == 'api/main/products/create-manual/') {
          created.add((b as Map).cast<String, dynamic>());
          return {'id': 'n${created.length}'};
        }
        if (m == 'GET' && p.contains('suppliers')) return <dynamic>[];
        if (m == 'GET' && p.contains('products/list')) return {'count': 0, 'results': <dynamic>[]};
        return <String, dynamic>{};
      });
    });
    tearDown(() => db.close());

    List<Override> overrides() => [databaseProvider.overrideWithValue(db), apiProvider.overrideWithValue(api), sessionProvider.overrideWith(() => _Fixed(_owner))];

    Future<void> open(WidgetTester t, {String lang = 'ru', bool dark = false}) async {
      await t.pumpWidget(_app(const NewItemScreen(), lang: lang, dark: dark, overrides: overrides()));
      await t.pumpAndSettle();
    }

    Future<void> save(WidgetTester t) async {
      final btn = find.widgetWithText(FilledButton, 'Сохранить');
      await _scrollTo(t, btn);
      await t.tap(btn);
      await t.pumpAndSettle();
    }

    testWidgets('цена, количество и единица — выше поставщика и доп. штрихкодов; кг 0,5 и цена 12,75 → весовой товар, остаток 0.5', (t) async {
      _phone(t, h: 900);
      await open(t);
      final fields = find.byType(TextField);
      await t.enterText(fields.at(1), 'Огурцы');
      await t.enterText(find.byKey(const Key('newItemPrice')), '12,75');
      expect(find.byKey(const Key('newItemQty')), findsOneWidget);
      // количество и единица видны на первом экране, а не внизу после поставщика и доп. штрихкодов
      expect(t.getTopLeft(find.byKey(const Key('newItemQty'))).dy, lessThan(900));
      expect(find.byKey(const Key('unit_кг')).hitTestable(), findsOneWidget);
      await _scrollTo(t, find.byKey(const Key('unit_кг')));
      await t.tap(find.byKey(const Key('unit_кг')));
      await t.pumpAndSettle();
      expect(find.text('Килограмм: можно дробное количество, например 0,5'), findsOneWidget);
      await _scrollTo(t, find.byKey(const Key('newItemQty')));
      await t.enterText(find.byKey(const Key('newItemQty')), '0,5');
      await save(t);
      expect(created.length, 1);
      expect(created.single['unit'], 'кг');
      expect(created.single['is_weight'], true);
      expect(created.single['price'], '12.75');
      expect(api.bodiesOf('PATCH', 'api/main/products/n1').single['quantity'], 0.5);
    });

    testWidgets('«шт» с количеством 2,5 — ошибка под полем, на сервер ничего не ушло; литр с 2,5 — можно', (t) async {
      _phone(t, h: 900);
      await open(t);
      await t.enterText(find.byType(TextField).at(1), 'Тетрадь');
      await t.enterText(find.byKey(const Key('newItemPrice')), '20');
      await t.enterText(find.byKey(const Key('newItemQty')), '2,5');
      await t.pump();
      String? qtyError() => t.widget<TextField>(find.byKey(const Key('newItemQty'))).decoration?.errorText;
      expect(qtyError(), contains('количество должно быть целым'));
      await save(t);
      await _scrollTo(t, find.byKey(const Key('newItemQty')));
      expect(qtyError(), contains('количество должно быть целым'));
      expect(created, isEmpty);
      await _scrollTo(t, find.byKey(const Key('unit_л')));
      await t.tap(find.byKey(const Key('unit_л')));
      await t.pumpAndSettle();
      await _scrollTo(t, find.byKey(const Key('newItemQty')));
      expect(qtyError(), isNull);
      await save(t);
      expect(created.single['unit'], 'л');
      expect(created.single['is_weight'], false);
      expect(api.bodiesOf('PATCH', 'api/main/products/n1').single['quantity'], 2.5);
    });

    testWidgets('без цены: подпись «Укажите цену продажи», на сервер ничего не ушло', (t) async {
      _phone(t, h: 900);
      await open(t);
      await t.enterText(find.byType(TextField).at(1), 'Хлеб');
      await save(t);
      expect(created, isEmpty);
      await t.drag(find.byType(Scrollable).first, const Offset(0, 3000));
      await t.pumpAndSettle();
      expect(find.text('Укажите цену продажи'), findsWidgets);
    });

    testWidgets('своя единица «рулон»: выбор через окно, уходит как есть', (t) async {
      _phone(t, h: 900);
      await open(t);
      await t.enterText(find.byType(TextField).at(1), 'Плёнка');
      await t.enterText(find.byKey(const Key('newItemPrice')), '300');
      await t.enterText(find.byKey(const Key('newItemQty')), '4');
      await _scrollTo(t, find.byKey(const Key('unit_custom')));
      await t.tap(find.byKey(const Key('unit_custom')));
      await t.pumpAndSettle();
      await t.enterText(find.byKey(const Key('unitCustomField')), 'рулон');
      await t.tap(find.widgetWithText(FilledButton, 'Готово'));
      await t.pumpAndSettle();
      expect(find.byKey(const Key('unit_custom_value')), findsOneWidget);
      await save(t);
      expect(created.single['unit'], 'рулон');
    });

    testWidgets('штрихкод только цифры: буквы отбрасываются, 13 цифр сохраняются, вставка «487 000-115 9955» → 4870001159955', (t) async {
      _phone(t, h: 900);
      await open(t);
      final field = find.byKey(const Key('newItemBarcode'));
      String text() => t.widget<TextField>(field).controller!.text;
      await t.enterText(field, 'abcXYZ');
      expect(text(), '');
      await t.enterText(field, '4870001159955');
      expect(text(), '4870001159955');
      await t.enterText(field, '487 000-115 9955');
      expect(text(), '4870001159955', reason: 'вставка очищается от пробелов и дефисов');
      await t.enterText(field, '48700011599551234567');
      expect(text().length, 14, reason: 'длина не больше 14');
      await t.enterText(field, '4870001159955');
      await t.enterText(find.byType(TextField).at(1), 'Чай');
      await t.enterText(find.byKey(const Key('newItemPrice')), '80');
      await t.enterText(find.byKey(const Key('newItemQty')), '3');
      await save(t);
      expect(created.single['barcode'], '4870001159955');
    });

    testWidgets('штрихкод из 2 цифр — подсказка об ошибке и товар не создаётся; поле «Доп. штрихкод» тоже только цифры', (t) async {
      _phone(t, h: 900);
      await open(t);
      await t.enterText(find.byKey(const Key('newItemBarcode')), '12');
      await t.pump();
      expect(find.text('Штрихкод — от 4 до 14 цифр, без букв'), findsWidgets);
      await t.enterText(find.byType(TextField).at(1), 'Чай');
      await t.enterText(find.byKey(const Key('newItemPrice')), '80');
      await save(t);
      expect(created, isEmpty);
      await _scrollTo(t, find.text('Добавить штрихкод'));
      await t.tap(find.text('Добавить штрихкод'));
      await t.pumpAndSettle();
      final alt = find.widgetWithText(TextField, 'Штрихкод');
      await t.enterText(alt.first, 'ab 12-34');
      expect(t.widget<TextField>(alt.first).controller!.text, '1234');
    });

    for (final lang in ['ru', 'ky', 'uz']) {
      for (final dark in [false, true]) {
        testWidgets('360 dp, шрифт 1,3, $lang${dark ? ', тёмная' : ''}: цена, количество и единицы без переполнений', (t) async {
          _phone(t, scale: 1.3, h: 800);
          await open(t, lang: lang, dark: dark);
          await _scrollTo(t, find.byKey(const Key('unit_мл')));
          expect(find.byKey(const Key('unitHint')), findsOneWidget);
          expect(t.getSize(find.byKey(const Key('unit_custom'))).height, greaterThanOrEqualTo(32));
          await t.tap(find.byKey(const Key('unit_кг')));
          await t.pumpAndSettle();
          expect(t.takeException(), isNull);
          expect(_clipped(t), isEmpty);
        });
      }
    }
  });

  group('Карточка товара: единицы', () {
    late AppDatabase db;
    late _Api api;
    late Map<String, dynamic> server;

    setUp(() {
      db = AppDatabase(NativeDatabase.memory());
      server = {
        'id': 'p1',
        'name': 'Молоко',
        'price': '85.00',
        'purchase_price': '70.00',
        'markup_percent': '21',
        'wholesale_price': '0',
        'quantity': '12.5',
        'unit': 'шт',
        'barcode': '4700000000011',
        'alternate_barcodes': [],
        'packages': <dynamic>[],
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
      name: 'Молоко',
      price: 85,
      wholesalePrice: 0,
      barcode: '4700000000011',
      altBarcodes: '',
      unit: 'шт',
      isWeight: false,
      isService: false,
      isFavorite: false,
      quantity: 12.5,
      category: '',
      searchKey: 'молоко',
      promoRules: '',
      minQuantity: 0,
    );

    Future<void> open(WidgetTester t, {String lang = 'ru', bool dark = false}) async {
      await t.pumpWidget(
        _app(
          ProductEditScreen(product: product),
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

    testWidgets('единица «л»: в карточке есть мл, м, пара, компл; выбрали л → PATCH unit=л, is_weight=false', (t) async {
      _phone(t, h: 900);
      await open(t);
      for (final u in ['шт', 'кг', 'г', 'л', 'мл', 'м', 'упак', 'пара', 'компл']) {
        expect(find.byKey(Key('unit_$u')), findsOneWidget, reason: u);
      }
      await _scrollTo(t, find.byKey(const Key('unit_л')));
      await t.tap(find.byKey(const Key('unit_л')));
      await t.pumpAndSettle();
      expect(find.text('Литр: можно дробное количество, например 0,5'), findsOneWidget);
      final save = find.widgetWithText(FilledButton, 'Сохранить');
      await _scrollTo(t, save);
      await t.tap(save);
      await t.pumpAndSettle();
      final patch = api.bodiesOf('PATCH', 'api/main/products/p1').single;
      expect(patch['unit'], 'л');
      expect(patch['is_weight'], false);
    });

    testWidgets('штрихкод и доп. штрихкод в карточке только цифры; вставка очищается; короткий код не сохраняется', (t) async {
      _phone(t, h: 900);
      await open(t);
      final field = find.byKey(const Key('editBarcode'));
      await _scrollTo(t, field);
      String text() => t.widget<TextField>(field).controller!.text;
      await t.enterText(field, 'x487 000-115 9955y');
      expect(text(), '4870001159955');
      await t.enterText(field, '12');
      await t.pump();
      expect(find.text('Штрихкод — от 4 до 14 цифр, без букв'), findsOneWidget);
      final save = find.widgetWithText(FilledButton, 'Сохранить');
      await _scrollTo(t, save);
      await t.tap(save);
      await t.pumpAndSettle();
      expect(api.bodiesOf('PATCH', 'api/main/products/p1'), isEmpty);
      await _scrollTo(t, field);
      await t.enterText(field, '4700000000028');
      await _scrollTo(t, save);
      await t.tap(save);
      await t.pumpAndSettle();
      expect(api.bodiesOf('PATCH', 'api/main/products/p1').single['barcode'], '4700000000028');
    });

    for (final lang in ['ru', 'ky', 'uz']) {
      testWidgets('360 dp, шрифт 1,3, $lang: единицы карточки без переполнений', (t) async {
        _phone(t, scale: 1.3, h: 800);
        await open(t, lang: lang, dark: lang == 'uz');
        await _scrollTo(t, find.byKey(const Key('unit_мл')));
        expect(t.takeException(), isNull);
        expect(_clipped(t, find.byType(UnitPicker)), isEmpty);
      });
    }
  });

  group('Массовая загрузка: экран', () {
    late AppDatabase db;
    late _Api api;
    var seq = 0;

    setUp(() {
      seq = 0;
      db = AppDatabase(NativeDatabase.memory());
      api = _Api((m, p, b) async {
        if (m == 'POST' && p == 'api/main/products/create-manual/') return {'id': 'n${++seq}'};
        if (m == 'GET' && p == 'api/main/products/list/') return {'count': 0, 'results': <dynamic>[]};
        if (m == 'GET' && p.startsWith('api/main/products/n')) return {'id': p.split('/')[3], 'quantity': '0'};
        return <String, dynamic>{};
      });
    });
    tearDown(() => db.close());

    Future<void> open(WidgetTester t, String csv, {String lang = 'ru', bool dark = false}) async {
      TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger.setMockMethodCallHandler(SystemChannels.platform, (c) async => c.method == 'Clipboard.getData' ? {'text': csv} : null);
      addTearDown(() => TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger.setMockMethodCallHandler(SystemChannels.platform, (c) async => null));
      await t.pumpWidget(
        _app(
          const BulkImportScreen(),
          lang: lang,
          dark: dark,
          overrides: [
            databaseProvider.overrideWithValue(db),
            apiProvider.overrideWithValue(api),
            sessionProvider.overrideWith(() => _Fixed(_owner)),
            globalProductsProvider.overrideWithValue(_Catalog()),
          ],
        ),
      );
      await t.pumpAndSettle();
      await t.tap(find.byIcon(Icons.content_paste_rounded));
      await t.pumpAndSettle();
    }

    const csv = 'Название;Цена;Закупка;Количество;Ед. изм.\nМолоко;85;70;1,5;литр\nХлеб;;10;5;шт\nМасло;300;250;2,5;шт';

    testWidgets('ошибки строк подсвечены; «С ошибками» → «Установить единицу кг» исправляет дробную; «Установить цену» — пустую', (t) async {
      _phone(t, h: 1000);
      await open(t, csv);
      expect(find.text('укажите цену продажи'), findsOneWidget);
      expect(find.textContaining('должно быть целым для «шт»'), findsOneWidget);
      await t.tap(find.byKey(const Key('bkSelectBad')));
      await t.pumpAndSettle();
      expect(find.text('Выбрано строк: 2'), findsOneWidget);
      await t.tap(find.byKey(const Key('bkMassPrice')));
      await t.pumpAndSettle();
      await t.enterText(find.byType(TextField).last, '12,75');
      await t.tap(find.widgetWithText(FilledButton, 'Применить к 2'));
      await t.pumpAndSettle();
      expect(find.text('укажите цену продажи'), findsNothing);
      expect(find.text(money(12.75)), findsNWidgets(2));
      // единица для строки с дробным количеством
      await t.tap(find.byKey(const Key('bkSelectBad')));
      await t.pumpAndSettle();
      expect(find.text('Выбрано строк: 1'), findsOneWidget);
      await t.tap(find.byKey(const Key('bkMassUnit')));
      await t.pumpAndSettle();
      await t.tap(find.byKey(const Key('unit_кг')));
      await t.pumpAndSettle();
      await t.tap(find.byKey(const Key('bkMassApply')));
      await t.pumpAndSettle();
      expect(find.textContaining('должно быть целым'), findsNothing);
      expect(find.text('2,5 кг'), findsWidgets);
    });

    testWidgets('правка одной строки: «Строка товара» с количеством 0,5, итог «Готово 2, с ошибками 1»', (t) async {
      _phone(t, h: 1000);
      await open(t, csv);
      // Масло (дробное «шт») оставляем с ошибкой, «Хлеб» без цены чиним через окно строки
      await _scrollTo(t, find.text('Хлеб'));
      await t.tap(find.text('Хлеб'));
      await t.pumpAndSettle();
      await t.enterText(find.byKey(const Key('bkPrice')), '35,5');
      await t.tap(find.byKey(const Key('bkRowSave')));
      await t.pumpAndSettle();
      final upload = find.widgetWithText(FilledButton, 'Загрузить 2');
      await t.ensureVisible(upload);
      await t.pumpAndSettle();
      await t.tap(upload);
      await t.pumpAndSettle();
      expect(find.byKey(const Key('bkResult')), findsOneWidget);
      expect(find.text('Готово 2, с ошибками 1'), findsOneWidget);
      final created = api.bodiesOf('POST', 'create-manual');
      expect(created.map((b) => b['name']), containsAll(['Молоко', 'Хлеб']));
      expect(created.firstWhere((b) => b['name'] == 'Хлеб')['price'], '35.50');
      expect(created.firstWhere((b) => b['name'] == 'Молоко')['unit'], 'л');
    });

    testWidgets('в окне строки штрихкод только цифры; в файле пробелы и дефисы убираются, буквы — ошибка строки', (t) async {
      _phone(t, h: 1000);
      final rows = BulkParser.parse(BulkParser.readText('Название;Штрихкод;Цена\nА;487 000-115 9955;10\nБ;AB123;10\nВ;12;10'));
      expect(rows[0].barcode, '4870001159955');
      expect(rows[0].valid, isTrue);
      expect(rows[1].problems, [RowProblem.badBarcode]);
      expect(rows[2].problems, [RowProblem.badBarcode]);
      await open(t, 'Название;Штрихкод;Цена\nХлеб;4870001159955;10');
      await _scrollTo(t, find.text('Хлеб'));
      await t.tap(find.text('Хлеб'));
      await t.pumpAndSettle();
      final f = find.byKey(const Key('bkBarcode'));
      await t.enterText(f, 'z 1234-5678');
      expect(t.widget<TextField>(f).controller!.text, '12345678');
    });

    for (final lang in ['ru', 'ky', 'uz']) {
      for (final dark in [false, true]) {
        testWidgets('360 dp, шрифт 1,3, $lang${dark ? ', тёмная' : ''}: список, массовые кнопки и окно строки без переполнений', (t) async {
          _phone(t, scale: 1.3, h: 900);
          await open(t, csv, lang: lang, dark: dark);
          await t.tap(find.byKey(const Key('bkSelectAll')));
          await t.pumpAndSettle();
          expect(t.takeException(), isNull);
          expect(_clipped(t, find.byType(ListView)), isEmpty);
          for (final k in ['bkMassUnit', 'bkMassPrice', 'bkMassQty']) {
            await t.ensureVisible(find.byKey(Key(k)));
            expect(t.getSize(find.byKey(Key(k))).height, greaterThanOrEqualTo(48), reason: k);
          }
          await _scrollTo(t, find.text('Молоко'));
          await t.tap(find.text('Молоко'));
          await t.pumpAndSettle();
          expect(find.byKey(const Key('bkRowSave')), findsOneWidget);
          expect(t.takeException(), isNull);
        });
      }
    }
  });
}
