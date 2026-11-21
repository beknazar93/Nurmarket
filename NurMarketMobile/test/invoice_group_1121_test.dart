// 2026-11-20 (владелец: «при загрузке накладных через наш ИИ: «Асу» и «Асу со вкусом клубники» — один товар, цена одна, марка одна,
// отличается только вкус»): разбор ответа ИИ с base/variant, группировка вариантов в один товар (основной штрихкод — первого варианта,
// остальные — дополнительные с названием варианта), вопрос «Объединить? цены разные», добавление вкуса существующему товару,
// «Своя команда ИИ», ручное «Разъединить»/«Объединить с…» и экран. Сети и боевой базы нет.
import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:drift/drift.dart' show Value;
import 'package:drift/native.dart';
import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/ai/ai_access.dart';
import 'package:nurmarket/core/ai/cam_aliases.dart';
import 'package:nurmarket/core/ai/vision.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/providers.dart';
import 'package:nurmarket/core/theme/theme.dart';
import 'package:nurmarket/core/util/audit.dart';
import 'package:nurmarket/features/scan/invoice_group.dart';
import 'package:nurmarket/features/scan/smart_camera.dart';
import 'package:nurmarket/features/stock/stock_repository.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

import 'admin_stock_fixes_test.dart' show FakeApi, StockServer, stockOf;

/// EAN-13 с верной контрольной цифрой по 12 цифрам.
String ean(String body12) {
  var sum = 0;
  for (var i = 0; i < 12; i++) {
    sum += (body12.codeUnitAt(i) - 48) * (i.isEven ? 1 : 3);
  }
  return '$body12${(10 - sum % 10) % 10}';
}

final _c1 = ean('460012345678'), _c2 = ean('460012345679'), _c3 = ean('460012345680');

SeenItem _asu(String variant, String code, double qty, {double price = 85, String brand = 'Асу', String base = 'Асу', String? name}) => SeenItem(
  name: name ?? (variant.isEmpty ? 'Асу' : 'Асу со вкусом $variant'),
  base: base,
  variant: variant,
  brand: brand,
  barcode: code,
  count: qty.round(),
  labelPrice: price,
);

const _owner = Session(email: 'a@x.kg', access: '', refresh: '', role: 'owner', isOwner: true, perms: []);

class _Fake implements HttpClientAdapter {
  final calls = <RequestOptions>[];
  @override
  Future<ResponseBody> fetch(RequestOptions options, Stream<Uint8List>? requestStream, Future<void>? cancelFuture) async {
    calls.add(options);
    return ResponseBody.fromString(
      jsonEncode({
        'candidates': [
          {
            'content': {
              'role': 'model',
              'parts': [
                {'text': '[{"n":"Асу","q":2,"p":85}]'},
              ],
            },
          },
        ],
      }),
      200,
      headers: {Headers.contentTypeHeader: [Headers.jsonContentType]},
    );
  }

  @override
  void close({bool force = false}) {}
}

class _V extends VisionService {
  _V(super.api, super.db, {super.http});
  @override
  Future<Map<String, dynamic>?> cloudSecrets() async => {'TelegramAiKey': 'AIza-test'};
  @override
  Future<AiAccessPolicy> accessPolicy({bool force = false}) async => const AiAccessPolicy.unset();
}

/// Экран: вместо ИИ — готовый ответ, запоминает, с какими настройками его просили.
class _Vision2 extends VisionService {
  final List<SeenItem> out;
  final asked = <({bool group, String command})>[];
  _Vision2(super.api, super.db, this.out);
  @override
  Future<String?> key() async => 'k';
  @override
  Future<List<SeenItem>> recognize(Uint8List jpeg, {String mode = 'sale', String mime = 'image/jpeg'}) async {
    asked.add((group: invoiceGroup, command: aiCommand));
    return out;
  }
}

void main() {
  group('разбор ответа ИИ', () {
    test('base (g), variant (v), brand (m) — и короткими, и длинными ключами; без них поля пустые', () {
      final a = VisionService.parseSeen('[{"n":"Асу со вкусом клубники","g":"Асу","v":"клубника","m":"Асу","q":3,"u":"шт","p":85,"b":"4600123456789"},{"n":"Хлеб","q":1,"p":30}]');
      expect((a[0].base, a[0].variant, a[0].brand, a[0].count, a[0].labelPrice), ('Асу', 'клубника', 'Асу', 3, 85));
      expect((a[1].base, a[1].variant, a[1].brand), ('', '', ''));
      final b = VisionService.parseSeen('[{"name":"Асу лимон","base":"Асу","variant":"лимон","brand":"Асу","count":2}]');
      expect((b.single.base, b.single.variant, b.single.brand), ('Асу', 'лимон', 'Асу'));
    });

    test('запрос накладной: с группировкой просит g, v, m; без неё — прежний короткий; команда владельца добавляется и режется до 300 знаков', () {
      final on = VisionService.invoiceSystemText(group: true);
      final off = VisionService.invoiceSystemText(group: false);
      expect(on, contains('{"n","q","u","p","b"}'));
      expect(on, contains('g — базовое название'));
      expect(off, isNot(contains('g — базовое название')));
      expect(off.length, lessThan(on.length));
      final withCmd = VisionService.invoiceSystemText(group: true, command: '  бренд —   Асу ');
      expect(withCmd, contains('«бренд — Асу»'));
      expect(VisionService.invoiceSystemText(group: false, command: 'x' * 500), contains('«${'x' * 300}»'));
      expect(VisionService.invoiceSystemText(group: false, command: 'x' * 500), isNot(contains('x' * 301)));
    });

    test('поля recognize: по умолчанию группировки нет (запрос прежний), экран включает её и передаёт команду', () async {
      final fake = _Fake();
      final db = AppDatabase(NativeDatabase.memory());
      addTearDown(db.close);
      FlutterSecureStorage.setMockInitialValues({});
      VisionService.resetForTest();
      final api = FakeApi((m, p, b, q) async => throw ApiException('нет', status: 404))..attach(_owner);
      final v = _V(api, db, http: Dio()..httpClientAdapter = fake);
      await v.recognize(Uint8List.fromList([0xFF, 0xD8, 1, 2]), mode: 'invoice');
      String sys(int i) => (((fake.calls[i].data as Map)['systemInstruction'] as Map)['parts'] as List).single['text'] as String;
      expect(sys(0), isNot(contains('g — базовое название')));
      v
        ..invoiceGroup = true
        ..aiCommand = 'бренд — Асу';
      await v.recognize(Uint8List.fromList([0xFF, 0xD8, 1, 2]), mode: 'invoice');
      expect(sys(1), contains('g — базовое название'));
      expect(sys(1), contains('«бренд — Асу»'));
    });
  });

  group('группировка вариантов', () {
    test('Асу, Асу клубника, Асу лимон, цена одна: один товар, код первого варианта основной, количества отдельно, сумма общая', () {
      final out = groupInvoiceItems([_asu('', _c1, 5), _asu('клубника', _c2, 3), _asu('лимон', _c3, 2)]);
      expect(out.length, 1);
      final g = out.single;
      expect(g.name, 'Асу');
      expect(g.brand, 'Асу');
      expect(g.barcode, _c1, reason: 'основной код — первого варианта');
      expect(g.labelPrice, 85);
      expect(g.variants.map((v) => (v.label, v.barcode, v.qty)), [('', _c1, 5.0), ('клубника', _c2, 3.0), ('лимон', _c3, 2.0)]);
      expect(g.amount, 10, reason: 'в приход — сумма вариантов');
      expect(groupTotals(out), (products: 1, extra: 2));
    });

    test('строка без base и без бренда («Асу») присоединяется к «Асу» с вкусом: название берётся из базового названия', () {
      final out = groupInvoiceItems([
        const SeenItem(name: 'Асу', barcode: '', count: 5, labelPrice: 85),
        _asu('клубника', _c2, 3, brand: ''),
      ]);
      expect(out.single.name, 'Асу');
      expect(out.single.variants.length, 2);
    });

    test('допуск по цене: 85 и 85,4 — одна цена; 85 и 95 — разные: не объединяется само, вопрос «Объединить?»', () {
      expect(groupInvoiceItems([_asu('клубника', _c2, 3, price: 85), _asu('лимон', _c3, 2, price: 85.4)]).length, 1);
      final items = [_asu('клубника', _c2, 3, price: 85), _asu('лимон', _c3, 2, price: 95)];
      expect(groupInvoiceItems(items).length, 2);
      final conflicts = priceConflicts(items);
      expect(conflicts.length, 1);
      expect(conflicts.values.single.length, 2);
      expect(priceConflicts(groupInvoiceItems([_asu('клубника', _c2, 3), _asu('лимон', _c3, 2)])), isEmpty);
      expect(pricesClose(0, 99), isTrue, reason: 'цена не прочитана — не мешает');
    });

    test('разные бренды — разные товары; без вариантов — не объединяются; разные базовые названия — тоже', () {
      expect(groupInvoiceItems([_asu('клубника', _c2, 3, base: 'Чай', brand: 'Lipton', name: 'Чай Lipton клубника'), _asu('лимон', _c3, 2, base: 'Чай', brand: 'Ahmad', name: 'Чай Ahmad лимон')]).length, 2);
      expect(groupInvoiceItems([const SeenItem(name: 'Молоко', count: 2, labelPrice: 85), const SeenItem(name: 'Хлеб', count: 1, labelPrice: 85)]).length, 2);
      expect(groupInvoiceItems([const SeenItem(name: 'Хлеб белый', barcode: '1', count: 2, labelPrice: 30), const SeenItem(name: 'Хлеб белый', barcode: '2', count: 1, labelPrice: 30)]).length, 2,
          reason: 'одинаковое название с разными кодами и без вариантов — разные товары');
      expect(groupInvoiceItems([_asu('клубника', _c2, 3), _asu('лимон', _c3, 2, base: 'Фанта', name: 'Фанта лимон')]).length, 2);
    });

    test('«не объединяй»: строки остаются как есть; та же строка на двух фото складывается по количеству', () {
      final items = [_asu('', _c1, 5), _asu('клубника', _c2, 3)];
      expect(groupInvoiceItems(items, merge: false).length, 2);
      final twice = groupInvoiceItems([_asu('клубника', _c2, 3), _asu('клубника', _c2, 4), _asu('лимон', _c3, 1)]);
      expect(twice.single.variants.map((v) => (v.label, v.qty)), [('клубника', 7.0), ('лимон', 1.0)]);
    });

    test('вариант без штрихкода остаётся в группе (код привяжут сканером), основной код — первого с кодом', () {
      final g = groupInvoiceItems([_asu('клубника', '', 3), _asu('лимон', _c3, 2)]).single;
      expect(g.variants.length, 2);
      expect(g.barcode, _c3);
    });

    test('«Своя команда ИИ»: «не объединяй» главнее включённого переключателя, «объединяй вкусы» — выключенного, про бренд — ничего', () {
      expect(commandWantsGroup('не объединяй'), isFalse);
      expect(commandWantsGroup('Пожалуйста, не объединяй вкусы'), isFalse);
      expect(commandWantsGroup('объединяй вкусы'), isTrue);
      expect(commandWantsGroup('бренд — Асу'), isNull);
      expect(commandWantsGroup(''), isNull);
      expect(const InvoiceAiOptions(group: true, command: 'не объединяй').merge, isFalse);
      expect(const InvoiceAiOptions(group: false, command: 'объединяй вкусы').merge, isTrue);
      expect(const InvoiceAiOptions(group: false, command: 'бренд — Асу').merge, isFalse);
      expect(const InvoiceAiOptions().merge, isTrue);
    });
  });

  group('вкус существующего товара — доп. штрихкод с названием варианта', () {
    late AppDatabase db;
    late StockServer s;
    late FakeApi api;
    final patches = <Object?>[];

    setUp(() {
      db = AppDatabase(NativeDatabase.memory());
      s = StockServer();
      patches.clear();
      api = FakeApi((m, p, b, q) async {
        if (m == 'PATCH') patches.add(b);
        return s.handle(m, p, b, q);
      })..attach(_owner);
    });
    tearDown(() => db.close());

    test('у «Асу» уже есть основной код: новый вкус уходит дополнительным с названием «клубника»; на телефоне — «код|0|клубника»', () async {
      final id = s.add('Асу', barcode: _c1, qty: 3);
      await db.upsertProduct(ProductsCompanion.insert(id: id, name: 'Асу', barcode: Value(_c1), quantity: const Value(3), searchKey: const Value('асу')));
      final repo = StockRepository(api, db);
      expect(await repo.queueBarcodeBind((await db.productById(id))!, _c2, name: 'клубника'), isTrue);
      for (var i = 0; i < 20; i++) {
        await Future<void>.delayed(const Duration(milliseconds: 10));
      }
      expect(patches.single, {
        'alternate_barcodes': [
          {'barcode': _c2, 'name': 'клубника'},
        ],
      });
      expect((await db.productById(id))!.altBarcodes, '$_c2|0|клубника');
      expect((await db.productByScan(_c2))?.product.id, id, reason: 'сканер находит товар по коду вкуса');
    });

    test('сопоставление по base+бренд: позиция-группа «Асу» находит существующий товар «Асу»', () async {
      await db.upsertProduct(ProductsCompanion.insert(id: 'a1', name: 'Асу', quantity: const Value(3), searchKey: const Value('асу')));
      await db.upsertProduct(ProductsCompanion.insert(id: 'x1', name: 'Фанта апельсин', quantity: const Value(3), searchKey: const Value('фанта апельсин')));
      FlutterSecureStorage.setMockInitialValues({});
      VisionService.resetForTest();
      CamAliases.resetForTest();
      final head = groupInvoiceItems([_asu('', _c1, 5), _asu('клубника', _c2, 3)]).single;
      final m = (await VisionService(api, db).match([head])).single;
      expect(m.picked?.id, 'a1');
    });
  });

  group('экран накладной', () {
    late AppDatabase db;
    late StockServer s;
    late FakeApi api;
    late List<Map<String, dynamic>> created;
    late List<Object?> patches;

    setUp(() {
      Audit.testDirectory = Directory.systemTemp.createTempSync('nm_group1121_');
      Audit.resetForTests();
      db = AppDatabase(NativeDatabase.memory());
      s = StockServer();
      created = [];
      patches = [];
      FlutterSecureStorage.setMockInitialValues({});
      VisionService.resetForTest();
      CamAliases.resetForTest();
      api = FakeApi((m, p, b, q) async {
        if (m == 'PATCH') patches.add(b);
        if (m == 'POST' && p == 'api/main/products/create-manual/') created.add((b as Map).cast<String, dynamic>());
        return s.handle(m, p, b, q);
      })..attach(_owner);
    });
    tearDown(() => db.close());

    Future<_Vision2> pumpScreen(
      WidgetTester tester,
      List<SeenMatch> matches, {
      Size size = const Size(411, 891),
      double scale = 1.0,
      Locale locale = const Locale('ru'),
      List<SeenItem> ai = const [],
      Future<List<CamPicked>> Function()? images,
    }) async {
      tester.view.physicalSize = size;
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.reset);
      final vision = _Vision2(api, db, ai);
      await tester.pumpWidget(
        ProviderScope(
          overrides: [databaseProvider.overrideWithValue(db), apiProvider.overrideWithValue(api), visionProvider.overrideWithValue(vision)],
          child: MaterialApp(
            theme: buildTheme(dark: false),
            locale: locale,
            supportedLocales: AppLocalizations.supportedLocales,
            localizationsDelegates: const [AppLocalizations.delegate, GlobalMaterialLocalizations.delegate, GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate],
            builder: (c, child) => MediaQuery(data: MediaQuery.of(c).copyWith(textScaler: TextScaler.linear(scale)), child: child!),
            home: SmartCameraScreen(debugMatches: matches, debugMode: 'invoice', debugPickImages: images),
          ),
        ),
      );
      await tester.pump();
      return vision;
    }

    Future<void> settle(WidgetTester tester) async {
      for (var i = 0; i < 14; i++) {
        await tester.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 40)));
        await tester.pump();
      }
    }

    SeenMatch groupMatch({Product? p}) {
      final head = groupInvoiceItems([_asu('', _c1, 5), _asu('клубника', _c2, 3), _asu('лимон', '', 2)]).single;
      return SeenMatch(head, p == null ? const [] : [(p, 1.0)]);
    }

    Finder bindButtons() => find.byWidgetPredicate((w) => w.key is ValueKey && '${(w.key as ValueKey).value}'.startsWith('camBind_'));

    testWidgets('группа — одна карточка «Асу»: варианты с кодом и количеством, итог «Товаров: 1, вариантов (доп. кодов): 2», у варианта без кода — привязка', (tester) async {
      await pumpScreen(tester, [groupMatch()]);
      expect(find.byKey(const Key('cam_variants_title')), findsOneWidget);
      expect(find.text('Варианты: 3'), findsOneWidget);
      expect(find.text('Обычный'), findsOneWidget);
      expect(find.text('со вкусом клубники'), findsNothing);
      expect(find.text('клубника'), findsOneWidget);
      expect(find.text('лимон'), findsOneWidget);
      expect(find.text('Штрихкод $_c1'), findsOneWidget);
      expect(find.text('Штрихкод $_c2'), findsOneWidget);
      expect(bindButtons(), findsOneWidget, reason: 'у «лимон» нет кода — привязка, как у обычной строки');
      expect(find.byKey(const Key('cam_group_summary')), findsOneWidget);
      expect(find.text('Товаров: 1, вариантов (доп. кодов): 2'), findsOneWidget);
      expect(find.text('Загрузить 1 товар на склад'), findsOneWidget);
    });

    testWidgets('загрузка нового товара: основной код первого варианта, остальные — alternate_barcodes объектами с названием варианта, остаток — сумма', (tester) async {
      await pumpScreen(tester, [SeenMatch(groupInvoiceItems([_asu('', _c1, 5), _asu('клубника', _c2, 3), _asu('лимон', _c3, 2)]).single, const [])]);
      await tester.tap(find.widgetWithText(FilledButton, 'Загрузить 1 товар на склад'));
      await settle(tester);
      await tester.pump(const Duration(milliseconds: 50)); // запись журнала аудита (разбивка по вариантам) стоит за нулевым таймером
      expect(created.length, 1);
      final c = created.single;
      expect(c['name'], 'Асу');
      expect(c['barcode'], _c1);
      expect(c['alternate_barcodes'], [
        {'barcode': _c2, 'name': 'клубника'},
        {'barcode': _c3, 'name': 'лимон'},
      ]);
      expect(stockOf(s, s.products.keys.single), 10, reason: '5 + 3 + 2 общим количеством');
    });

    testWidgets('существующий «Асу»: новые вкусы — дополнительные штрихкоды с названием варианта, остаток — сумма, разбивка в журнале', (tester) async {
      final id = s.add('Асу', barcode: _c1, qty: 3);
      await db.upsertProduct(ProductsCompanion.insert(id: id, name: 'Асу', barcode: Value(_c1), quantity: const Value(3), searchKey: const Value('асу')));
      final p = (await db.productById(id))!;
      await pumpScreen(tester, [SeenMatch(groupInvoiceItems([_asu('', _c1, 5), _asu('клубника', _c2, 3), _asu('лимон', _c3, 2)]).single, [(p, 1.0)])]);
      await tester.tap(find.widgetWithText(FilledButton, 'Загрузить 1 товар на склад'));
      await settle(tester);
      await tester.pump(const Duration(milliseconds: 50));
      expect(created, isEmpty, reason: 'товар уже есть — новый не создаётся');
      expect(stockOf(s, id), 13, reason: '3 + (5 + 3 + 2)');
      final alt = patches.whereType<Map>().where((m) => m.containsKey('alternate_barcodes')).toList();
      expect(alt, isNotEmpty);
      final all = [for (final m in alt) ...(m['alternate_barcodes'] as List)];
      expect(all.any((a) => (a as Map)['barcode'] == _c2 && a['name'] == 'клубника'), isTrue);
      expect(all.any((a) => (a as Map)['barcode'] == _c3 && a['name'] == 'лимон'), isTrue);
      final rows = await db.select(db.outbox).get();
      expect(rows.where((r) => r.kind == 'barcode').length, 2, reason: 'код основного варианта у товара уже есть — записей две');
    });

    testWidgets('«Разъединить»: первый вариант остаётся, остальные — отдельные строки со своими количествами, итог пропадает', (tester) async {
      await pumpScreen(tester, [SeenMatch(groupInvoiceItems([_asu('', _c1, 5), _asu('клубника', _c2, 3), _asu('лимон', _c3, 2)]).single, const [])]);
      final splitBtn = find.byWidgetPredicate((w) => w.key is ValueKey && '${(w.key as ValueKey).value}'.startsWith('cam_split_'));
      await tester.scrollUntilVisible(splitBtn, 150, scrollable: find.descendant(of: find.byType(ListView), matching: find.byType(Scrollable)).first);
      await tester.ensureVisible(splitBtn);
      await tester.pump();
      await tester.tap(splitBtn);
      await settle(tester);
      expect(find.byKey(const Key('cam_variants_title')), findsNothing);
      expect(find.byKey(const Key('cam_group_summary')), findsNothing);
      expect(find.text('Загрузить 3 товара на склад'), findsOneWidget);
    });

    testWidgets('«Объединить с…»: две строки ИИ без группировки → один товар «Асу» с вариантами «Обычный» и «со вкусом клубники»', (tester) async {
      await pumpScreen(tester, [
        SeenMatch(const SeenItem(name: 'Асу', barcode: '', count: 5, labelPrice: 85), const []),
        SeenMatch(const SeenItem(name: 'Асу со вкусом клубники', barcode: '', count: 3, labelPrice: 85), const []),
      ]);
      final menu = find.byWidgetPredicate((w) => w.key is ValueKey && '${(w.key as ValueKey).value}'.startsWith('cam_menu_')).first;
      await tester.tap(menu);
      await tester.pumpAndSettle();
      await tester.tap(find.text('Объединить с…'));
      await tester.pumpAndSettle();
      expect(find.text('С каким товаром объединить?'), findsOneWidget);
      await tester.tap(find.byWidgetPredicate((w) => w.key is ValueKey && '${(w.key as ValueKey).value}'.startsWith('cam_merge_pick_')).first);
      await settle(tester);
      expect(find.text('Варианты: 2'), findsOneWidget);
      expect(find.text('Обычный'), findsOneWidget);
      expect(find.text('со вкусом клубники'), findsOneWidget);
      expect(find.text('Загрузить 1 товар на склад'), findsOneWidget);
    });

    testWidgets('цены разные: вопрос «Объединить? цены разные»; «Объединить» — одна карточка, «Не объединять» — вопрос уходит', (tester) async {
      final a = _asu('клубника', _c2, 3, price: 85), b = _asu('лимон', _c3, 2, price: 95);
      await pumpScreen(tester, [SeenMatch(a, const []), SeenMatch(b, const [])]);
      expect(find.textContaining('варианты с разными ценами (85 / 95 сом)'), findsOneWidget);
      await tester.tap(find.byWidgetPredicate((w) => w.key is ValueKey && '${(w.key as ValueKey).value}'.startsWith('cam_conflict_no_')));
      await settle(tester);
      expect(find.textContaining('варианты с разными ценами'), findsNothing);
      expect(find.text('Загрузить 2 товара на склад'), findsOneWidget);
    });

    testWidgets('цены разные: «Объединить» делает одну карточку с вариантами', (tester) async {
      await pumpScreen(tester, [SeenMatch(_asu('клубника', _c2, 3, price: 85), const []), SeenMatch(_asu('лимон', _c3, 2, price: 95), const [])]);
      await tester.tap(find.byWidgetPredicate((w) => w.key is ValueKey && '${(w.key as ValueKey).value}'.startsWith('cam_conflict_yes_')));
      await settle(tester);
      expect(find.text('Варианты: 2'), findsOneWidget);
      expect(find.text('Загрузить 1 товар на склад'), findsOneWidget);
    });

    testWidgets('«Команда ИИ»: переключатель и своя команда запоминаются (по аккаунту), сбрасываются; команда уходит в запрос', (tester) async {
      final raw = [_asu('', _c1, 5), _asu('клубника', _c2, 3)];
      final v = await pumpScreen(
        tester,
        const [],
        ai: raw,
        images: () async => [CamPicked(Uint8List(100), 'a.jpg')],
      );
      await tester.tap(find.byKey(const Key('cam_ai_opts')));
      await tester.pumpAndSettle();
      expect(find.text('Команда ИИ для накладной'), findsOneWidget);
      expect(tester.widget<SwitchListTile>(find.byKey(const Key('cam_group_switch'))).value, isTrue, reason: 'по умолчанию включено');
      await tester.enterText(find.byKey(const Key('cam_ai_command')), 'бренд — Асу');
      await tester.pump();
      await tester.tap(find.byKey(const Key('cam_group_switch')));
      await tester.pumpAndSettle();
      await tester.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 100)));
      expect(await db.getSetting('cam.invoice.group'), '0');
      expect(await db.getSetting('cam.invoice.command'), 'бренд — Асу');
      await tester.tap(find.text('Готово'));
      await tester.pumpAndSettle();
      // запрос: группировка выключена (переключатель), команда ушла; список остался несгруппированным
      await tester.tap(find.byKey(const Key('cam_gallery')));
      await settle(tester);
      expect(v.asked.single, (group: false, command: 'бренд — Асу'));
      expect(find.byKey(const Key('cam_variants_title')), findsNothing);
      // «Сбросить» — по умолчанию
      await tester.tap(find.byKey(const Key('cam_ai_opts')));
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const Key('cam_ai_reset')));
      await tester.pumpAndSettle();
      await tester.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 100)));
      expect(await db.getSetting('cam.invoice.group'), '1');
      expect(await db.getSetting('cam.invoice.command'), '');
    });

    testWidgets('группировка включена: ответ ИИ с base/variant превращается в одну карточку; «не объединяй» в команде — нет', (tester) async {
      final raw = [_asu('', _c1, 5), _asu('клубника', _c2, 3)];
      final v = await pumpScreen(tester, const [], ai: raw, images: () async => [CamPicked(Uint8List(100), 'a.jpg')]);
      await tester.tap(find.byKey(const Key('cam_gallery')));
      await settle(tester);
      expect(v.asked.single, (group: true, command: ''));
      expect(find.text('Варианты: 2'), findsOneWidget);
      expect(find.text('Загрузить 1 товар на склад'), findsOneWidget);
    });

    testWidgets('«не объединяй» в команде главнее переключателя: строки отдельно', (tester) async {
      await db.setSetting('cam.invoice.command', 'не объединяй');
      final raw = [_asu('', _c1, 5), _asu('клубника', _c2, 3)];
      final v = await pumpScreen(tester, const [], ai: raw, images: () async => [CamPicked(Uint8List(100), 'a.jpg')]);
      await settle(tester); // настройки прочитались
      await tester.tap(find.byKey(const Key('cam_gallery')));
      await settle(tester);
      expect(v.asked.single, (group: false, command: 'не объединяй'));
      expect(find.byKey(const Key('cam_variants_title')), findsNothing);
      expect(find.text('Загрузить 2 товара на склад'), findsOneWidget);
    });

    for (final size in const [Size(360, 640), Size(411, 891)]) {
      for (final lang in const ['ru', 'ky', 'uz']) {
        testWidgets('${size.width.toInt()}×${size.height.toInt()}, $lang, шрифт 1,3: карточка-группа без переполнения, кнопки ≥ 48', (tester) async {
          await pumpScreen(tester, [groupMatch()], size: size, scale: 1.3, locale: Locale(lang));
          expect(tester.takeException(), isNull);
          final l = lookupAppLocalizations(Locale(lang));
          final scroll = find.descendant(of: find.byType(ListView), matching: find.byType(Scrollable)).first;
          await tester.scrollUntilVisible(find.text(l.camVariantsTitle(3)), 150, scrollable: scroll);
          expect(tester.takeException(), isNull);
          // нижняя часть карточки: «Разъединить» и «Объединить с…» (вторая — если строк больше одной; здесь строка одна)
          await tester.scrollUntilVisible(find.byWidgetPredicate((w) => w.key is ValueKey && '${(w.key as ValueKey).value}'.startsWith('cam_split_')), 150, scrollable: scroll);
          final split = tester.getRect(find.byWidgetPredicate((w) => w.key is ValueKey && '${(w.key as ValueKey).value}'.startsWith('cam_split_')));
          expect(split.height, greaterThanOrEqualTo(47.99));
          expect(split.right, lessThanOrEqualTo(size.width));
          await tester.scrollUntilVisible(bindButtons(), 100, scrollable: scroll);
          final bind = tester.getRect(bindButtons());
          expect(bind.height, greaterThanOrEqualTo(47.99));
          expect(bind.left, greaterThanOrEqualTo(0));
          expect(bind.right, lessThanOrEqualTo(size.width));
          expect(tester.takeException(), isNull);
          expect(find.text(l.camGroupSummary(1, 2)), findsOneWidget);
          final ai = find.byKey(const Key('cam_ai_opts'));
          expect(tester.getRect(ai).height, greaterThanOrEqualTo(47.99));
          expect(tester.getRect(ai).right, lessThanOrEqualTo(size.width));
        });

        testWidgets('${size.width.toInt()}×${size.height.toInt()}, $lang, шрифт 1,3: окно «Команда ИИ» целиком на экране, кнопки ≥ 48', (tester) async {
          await pumpScreen(tester, const [], size: size, scale: 1.3, locale: Locale(lang));
          final l = lookupAppLocalizations(Locale(lang));
          await tester.tap(find.byKey(const Key('cam_ai_opts')));
          await tester.pumpAndSettle();
          expect(tester.takeException(), isNull);
          for (final f in [find.byKey(const Key('cam_group_switch')), find.byKey(const Key('cam_ai_reset')), find.widgetWithText(FilledButton, l.done)]) {
            expect(f, findsOneWidget);
            final r = tester.getRect(f);
            expect(r.height, greaterThanOrEqualTo(47.99));
            expect(r.left, greaterThanOrEqualTo(0));
            expect(r.right, lessThanOrEqualTo(size.width));
            expect(r.bottom, lessThanOrEqualTo(size.height));
          }
          expect(find.text(l.camGroupToggle), findsOneWidget);
          expect(find.byKey(const Key('cam_ai_command')), findsOneWidget);
        });
      }
    }
  });
}
