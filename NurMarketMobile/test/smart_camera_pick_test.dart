// 2026-10-27 (владелец: «на фото существующий товар не находит»; «в накладной добавь выбор из галереи и из файла»):
// умная камера — общий матчер вместо порога 0,75 и поиска по 5 буквам, выбор из 5 вариантов («Это …?»), автовыбор только при
// оценке ≥ 0,85 и отрыве ≥ 0,15, штрихкод со снимка до ИИ, запоминание названий (алиасы), накладная из галереи и из файла
// (маршрутизация по расширению и права). Gemini не вызывается: ответы ИИ подставлены, сеть — фейк.
import 'dart:typed_data';

import 'package:drift/drift.dart' show Value;
import 'package:drift/native.dart';
import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';
import 'package:nurmarket/core/ai/cam_aliases.dart';
import 'package:nurmarket/core/ai/vision.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/providers.dart';
import 'package:nurmarket/core/theme/theme.dart';
import 'package:nurmarket/features/admin/invoice_import.dart' show InvoiceFileArg;
import 'package:nurmarket/features/scan/smart_camera.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

import 'admin_stock_fixes_test.dart' show FakeApi;

class _Vision extends VisionService {
  final List<SeenItem> out;
  final calls = <({String mode, String mime, int bytes})>[];
  _Vision(super.api, super.db, this.out);
  @override
  Future<String?> key() async => 'k';
  @override
  Future<List<SeenItem>> recognize(Uint8List jpeg, {String mode = 'sale', String mime = 'image/jpeg'}) async {
    calls.add((mode: mode, mime: mime, bytes: jpeg.length));
    return out;
  }
}

class _Fixed extends SessionController {
  final Session s;
  _Fixed(this.s);
  @override
  Session? build() => s;
}

const _owner = Session(email: 'a', access: '', refresh: '', role: 'owner', isOwner: true, perms: []);
// сотрудник со складом, но без «Закупок»: загрузить файл накладной нельзя
const _noProcure = Session(email: 'c', access: '', refresh: '', role: 'cashier', isOwner: false, perms: [Session.viewProducts]);

Future<void> _put(AppDatabase db, String id, String name, {String code = '', double qty = 5}) => db.upsertProduct(
  ProductsCompanion.insert(id: id, name: name, barcode: Value(code.isEmpty ? null : code), quantity: Value(qty), searchKey: Value('${name.toLowerCase()} $code')),
);

Product _p(String id, String name) => Product(
  id: id,
  name: name,
  price: 100,
  wholesalePrice: 0,
  promoRules: '',
  minQuantity: 0,
  quantity: 5,
  unit: 'шт',
  isWeight: false,
  isService: false,
  isFavorite: false,
  altBarcodes: '',
  category: '',
  searchKey: name.toLowerCase(),
);

void main() {
  late AppDatabase db;
  late FakeApi api;
  late List<String> log;
  Map<String, dynamic> shared = {};

  setUp(() async {
    CamAliases.resetForTest();
    db = AppDatabase(NativeDatabase.memory());
    await _put(db, 'p1', 'Адыгене 1л', code: '4700000310326');
    await _put(db, 'p2', 'ASU Негаз 1 л клуб-киви', code: '4870001571039');
    await _put(db, 'p3', 'ASU Негаз 1л персик', code: '4870001571528');
    await _put(db, 'p4', 'ASU Негаз 0,5 л персик', code: '4870001571535');
    await _put(db, 'p5', 'Спрайт 1,0л');
    await _put(db, 'p6', 'Sprite Мохито');
    shared = {};
    log = [];
    api = FakeApi((m, p, b, q) async {
      log.add('$m $p');
      if (p == 'api/main/app-settings/nurmarket-mobile/') {
        if (m == 'PATCH') shared.addAll((b as Map).cast<String, dynamic>());
        return {'settings': shared};
      }
      if (m == 'GET' && p == 'api/main/products/list/') return {'results': []};
      throw ApiException('не ожидали $m $p', status: 404);
    })..attach(_owner);
  });
  tearDown(() => db.close());

  group('автовыбор: «≥ 0,85 и отрыв ≥ 0,15»', () {
    SeenMatch m(List<double> scores) => SeenMatch(const SeenItem(name: 'x'), [for (var i = 0; i < scores.length; i++) (_p('p$i', 'Товар $i'), scores[i])]);

    test('уверенный, ничья, слабый', () {
      expect(m([0.95, 0.5]).picked?.id, 'p0', reason: 'отрыв 0,45');
      expect(m([1.0]).picked?.id, 'p0', reason: 'один кандидат');
      expect(m([0.9, 0.8]).picked, isNull, reason: 'отрыв 0,1 < 0,15 — ничья, выбирает человек');
      expect(m([0.84, 0.2]).picked, isNull, reason: 'ниже 0,85');
      expect(m([0.7, 0.6]).picked, isNull, reason: 'старый порог 0,75 не действует, но и 0,7 — не уверенно');
      expect(m([]).picked, isNull);
    });
  });

  group('сверка с каталогом по всему списку (VisionService.match)', () {
    Future<SeenMatch> match(SeenItem s) async => (await VisionService(api, db).match([s])).single;

    test('латиница из ответа ИИ находит русскую карточку: Adygene mineral water -> Адыгене 1л', () async {
      final r = await match(const SeenItem(brand: 'Adygene', name: 'mineral water', size: '1 L'));
      expect(r.picked?.name, 'Адыгене 1л');
    });

    test('«Асу» — четыре кандидата, сам не выбирается', () async {
      final r = await match(const SeenItem(name: 'Асу'));
      expect(r.picked, isNull);
      expect(r.candidates.length, 3, reason: 'три ASU в тестовой базе, остальные не подмешиваются');
      expect(r.candidates.every((c) => c.$1.name.startsWith('ASU')), isTrue);
    });

    test('многословный ответ ИИ (был 0,64 — «Новый»): теперь выбран', () async {
      final r = await match(const SeenItem(name: 'Вода питьевая негазированная со вкусом клубники и киви', brand: 'ASU', size: '1000 мл'));
      expect(r.picked?.name, 'ASU Негаз 1 л клуб-киви');
    });

    test('name_ru: «Sprite 1 L» + «Спрайт» -> Спрайт 1,0л, а не Sprite Мохито', () async {
      final r = await match(const SeenItem(name: 'Sprite', nameRu: 'Спрайт', size: '1 L'));
      expect(r.picked?.name, 'Спрайт 1,0л');
    });

    test('штрихкод от ИИ с верной контрольной цифрой — товар по коду', () async {
      final r = await match(const SeenItem(name: 'что-то странное', barcode: '4700000310326'));
      expect(r.picked?.name, 'Адыгене 1л');
    });

    test('ответ ИИ разбирается вместе с name_ru', () {
      final items = VisionService.parseSeen('[{"name":"Sprite","name_ru":"Спрайт","brand":"Coca-Cola","size":"1 L","barcode":"","count":2}]');
      expect(items.single.nameRu, 'Спрайт');
      expect(items.single.titleRu, 'Coca-Cola Спрайт 1 L');
    });
  });

  group('алиасы CamAliases', () {
    test('слияние без затирания, не больше 5 на товар, повторы без дублей', () {
      final a = {'p1': ['aaa', 'bbb']};
      final b = {'p1': ['bbb', 'ccc'], 'p2': ['ddd']};
      final r = CamAliases.merge(a, b);
      expect(r['p1'], ['aaa', 'bbb', 'ccc']);
      expect(r['p2'], ['ddd']);
      var big = <String, List<String>>{};
      for (var i = 0; i < 8; i++) {
        big = CamAliases.merge(big, {'p': ['name $i']});
      }
      expect(big['p'], ['name 3', 'name 4', 'name 5', 'name 6', 'name 7'], reason: 'остаются последние');
    });

    test('разбор мусора не падает; короткое и пустое отбрасывается', () {
      expect(CamAliases.parse('не json'), isEmpty);
      expect(CamAliases.parse({'p1': 'не список', 'p2': ['ab', '  Fanta Orange   0,5 л ']}), {'p2': ['fanta orange 0,5 л']});
    });

    test('запись: чужие алиасы с сервера не затираются, копия на телефоне есть', () async {
      shared['CamAliases'] = {'pX': ['чужое название']};
      final ok = await CamAliases.add(db, api, 'p1', 'Adygene Mineral Water');
      expect(ok, isTrue);
      final sent = CamAliases.parse(shared['CamAliases']);
      expect(sent['pX'], ['чужое название']);
      expect(sent['p1'], ['adygene mineral water']);
      expect(CamAliases.parse(await db.getSetting('cam.aliases'))['p1'], ['adygene mineral water']);
    });

    test('нет права записи (403): остаётся на телефоне, повторно сервер не мучается', () async {
      final denied = FakeApi((m, p, b, q) async {
        log.add('$m $p');
        if (m == 'PATCH') throw ApiException('нет прав', status: 403);
        return {'settings': <String, dynamic>{}};
      })..attach(_owner);
      expect(await CamAliases.add(db, denied, 'p1', 'первое название'), isFalse);
      expect(await CamAliases.add(db, denied, 'p1', 'второе название'), isFalse);
      expect(log.where((e) => e.startsWith('PATCH')).length, 1, reason: 'после 403 запись больше не пробуется');
      expect(CamAliases.current['p1'], ['первое название', 'второе название']);
    });

    test('нет связи: алиас на телефоне; выбранное в следующий раз даёт 1,0', () async {
      final off = FakeApi((m, p, b, q) async => throw ApiException('Нет связи'))..attach(_owner);
      expect(await CamAliases.add(db, off, 'p6', 'Sprite Lemon Lime fizzy 1 L'), isFalse);
      final vision = VisionService(api, db);
      final r = (await vision.match([const SeenItem(brand: 'Sprite', name: 'Lemon Lime fizzy', size: '1 L')])).single;
      expect(r.candidates.first.$1.name, 'Sprite Мохито');
      expect(r.candidates.first.$2, 1.0);
      expect(r.picked?.name, 'Sprite Мохито');
    });
  });

  group('экран умной камеры', () {
    Future<_Vision> pump(
      WidgetTester tester, {
      String? mode,
      String? initialMode,
      List<SeenMatch>? matches,
      Future<List<CamPicked>> Function()? images,
      Future<CamPicked?> Function()? file,
      Session session = _owner,
      List<SeenItem> ai = const [],
      Locale locale = const Locale('ru'),
      double scale = 1.0,
      bool dark = false,
      List<String>? routed,
    }) async {
      tester.view.physicalSize = const Size(360, 1600);
      tester.view.devicePixelRatio = 1;
      tester.platformDispatcher.textScaleFactorTestValue = scale;
      addTearDown(tester.view.reset);
      addTearDown(tester.platformDispatcher.clearAllTestValues);
      api.attach(session);
      final vision = _Vision(api, db, ai);
      final router = GoRouter(
        routes: [
          GoRoute(
            path: '/',
            builder: (_, _) => SmartCameraScreen(debugMode: mode, initialMode: initialMode, debugMatches: matches, debugPickImages: images, debugPickFile: file),
          ),
          GoRoute(
            path: '/admin/invoice',
            builder: (_, s) {
              final f = s.extra as InvoiceFileArg;
              routed?.add('${f.name}:${f.bytes.length}');
              return Scaffold(body: Text('INVOICE ${f.name}'));
            },
          ),
        ],
      );
      await tester.pumpWidget(
        ProviderScope(
          overrides: [
            databaseProvider.overrideWithValue(db),
            apiProvider.overrideWithValue(api),
            visionProvider.overrideWithValue(vision),
            sessionProvider.overrideWith(() => _Fixed(session)),
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
      return vision;
    }

    Future<void> settle(WidgetTester tester) async {
      for (var i = 0; i < 25; i++) {
        await tester.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 40)));
        await tester.pump();
      }
    }

    testWidgets('не уверен: до 5 вариантов «Это …?», выбор переносит позицию в «есть в базе» и запоминает название', (tester) async {
      final cands = [(_p('p2', 'ASU Негаз 1 л клуб-киви'), 0.8), (_p('p3', 'ASU Негаз 1л персик'), 0.78), (_p('p4', 'ASU Негаз 0,5 л персик'), 0.5), (_p('p1', 'Адыгене 1л'), 0.4)];
      await pump(tester, mode: 'shelf', matches: [SeenMatch(const SeenItem(brand: 'ASU', name: 'Still water', size: '1 L', count: 3), cands)]);
      expect(find.text('Похоже на — выберите, если это он'), findsOneWidget);
      for (final c in cands) {
        expect(find.byKey(ValueKey('cand_${c.$1.id}')), findsOneWidget);
        expect(tester.getSize(find.byKey(ValueKey('cand_${c.$1.id}'))).height, greaterThanOrEqualTo(48));
      }
      expect(find.text('Это «ASU Негаз 1 л клуб-киви»?'), findsOneWidget);
      await tester.tap(find.byKey(const ValueKey('cand_p2')));
      await settle(tester);
      // позиция ушла в «есть в базе»: кнопок выбора больше нет
      expect(find.byKey(const ValueKey('cand_p2')), findsNothing);
      expect(find.text('ASU Негаз 1 л клуб-киви'), findsOneWidget);
      // название ИИ запомнено (и ушло в общие настройки компании)
      expect(CamAliases.current['p2'], ['asu still water 1 l']);
      expect(CamAliases.parse(shared['CamAliases'])['p2'], ['asu still water 1 l']);
    });

    testWidgets('сам выбранный (≥ 0,85, отрыв ≥ 0,15) сразу «есть в базе», без вопроса', (tester) async {
      await pump(tester, mode: 'shelf', matches: [
        SeenMatch(const SeenItem(name: 'Adygene', count: 2), [(_p('p1', 'Адыгене 1л'), 0.95), (_p('p2', 'ASU Негаз 1 л клуб-киви'), 0.4)]),
      ]);
      expect(find.text('Похоже на — выберите, если это он'), findsNothing);
      expect(find.text('Адыгене 1л'), findsOneWidget);
    });

    testWidgets('«Накладная» по маршруту ?mode=invoice: кнопки «Из галереи» и «Из файла»; на «Полке» их нет', (tester) async {
      await pump(tester, initialMode: 'invoice');
      expect(find.byKey(const Key('cam_gallery')), findsOneWidget);
      expect(find.byKey(const Key('cam_file')), findsOneWidget);
      expect(find.text('Из галереи'), findsOneWidget);
      expect(find.text('Из файла'), findsOneWidget);
      expect(tester.getSize(find.byKey(const Key('cam_gallery'))).height, greaterThanOrEqualTo(48));
    });

    testWidgets('2026-10-27: на «Полке» и «Этикетках» только «Из галереи», «Из файла» — лишь в накладной', (tester) async {
      await pump(tester);
      expect(find.byKey(const Key('cam_gallery')), findsOneWidget);
      expect(find.byKey(const Key('cam_file')), findsNothing);
    });

    testWidgets('«Из галереи»: две страницы накладной -> два запроса к ИИ в режиме «invoice», одинаковый товар складывается', (tester) async {
      final v = await pump(
        tester,
        mode: 'invoice',
        ai: const [SeenItem(name: 'Адыгене 1л', count: 3)],
        images: () async => [CamPicked(Uint8List(100), 'a.jpg'), CamPicked(Uint8List(200), 'b.png', mime: 'image/png')],
      );
      await tester.tap(find.byKey(const Key('cam_gallery')));
      await settle(tester);
      expect(v.calls.map((c) => (c.mode, c.mime)), [('invoice', 'image/jpeg'), ('invoice', 'image/png')]);
      expect(find.text('Адыгене 1л'), findsOneWidget);
      expect(find.text('6'), findsOneWidget, reason: '3 + 3 штуки одного товара');
    });

    testWidgets('«Из файла»: PDF -> ИИ с типом application/pdf', (tester) async {
      final v = await pump(tester, mode: 'invoice', ai: const [SeenItem(name: 'Адыгене 1л', count: 2)], file: () async => CamPicked(Uint8List(1000), 'Накладная.PDF', mime: 'application/pdf'));
      await tester.tap(find.byKey(const Key('cam_file')));
      await settle(tester);
      expect(v.calls.single.mime, 'application/pdf');
      expect(v.calls.single.mode, 'invoice');
      expect(find.text('Адыгене 1л'), findsOneWidget);
    });

    for (final name in ['накладная.xlsx', 'a.CSV', 'b.xml', 'c.txt', 'd.tsv']) {
      testWidgets('«Из файла»: $name -> экран «Загрузить накладную» (право «Закупки»)', (tester) async {
        final routed = <String>[];
        final v = await pump(tester, mode: 'invoice', routed: routed, file: () async => CamPicked(Uint8List(321), name));
        await tester.tap(find.byKey(const Key('cam_file')));
        await settle(tester);
        expect(routed, ['$name:321']);
        expect(v.calls, isEmpty, reason: 'таблицу читает разбор файла, ИИ не нужен');
        expect(find.text('INVOICE $name'), findsOneWidget);
      });
    }

    testWidgets('«Из файла»: таблица без права «Закупки» — сообщение, перехода нет', (tester) async {
      final routed = <String>[];
      await pump(tester, mode: 'invoice', session: _noProcure, routed: routed, file: () async => CamPicked(Uint8List(10), 'x.xlsx'));
      await tester.tap(find.byKey(const Key('cam_file')));
      await settle(tester);
      expect(routed, isEmpty);
      expect(find.textContaining('с правом «Закупки»'), findsOneWidget);
    });

    testWidgets('«Из файла»: старый .xls, чужой тип и слишком большой PDF — понятные сообщения', (tester) async {
      var next = CamPicked(Uint8List(10), 'старая.xls');
      final v = await pump(tester, mode: 'invoice', file: () async => next);
      Future<void> pick() async {
        await tester.tap(find.byKey(const Key('cam_file')));
        await settle(tester);
      }

      await pick();
      expect(find.textContaining('.xlsx'), findsWidgets);
      next = CamPicked(Uint8List(10), 'письмо.docx');
      await pick();
      expect(find.textContaining('Такой файл не подходит'), findsOneWidget);
      next = CamPicked(Uint8List(8 * 1024 * 1024 + 1), 'огромный.pdf', mime: 'application/pdf');
      await pick();
      expect(find.textContaining('Файл больше 8 МБ'), findsOneWidget);
      expect(v.calls, isEmpty);
    });

    testWidgets('маршрутизация по расширению', (tester) async {
      expect(camFileRoute('a.XLSX'), CamFileRoute.table);
      expect(camFileRoute('a.pdf'), CamFileRoute.ai);
      expect(camFileRoute('a.jpeg'), CamFileRoute.ai);
      expect(camFileRoute('a.xls'), CamFileRoute.oldExcel);
      expect(camFileRoute('a'), CamFileRoute.unsupported);
      expect(camMimeOf('x.PDF'), 'application/pdf');
      expect(camMimeOf('x.png'), 'image/png');
    });

    testWidgets('штрихкод со снимка читается ДО ИИ: код есть в базе — запроса к Gemini нет', (tester) async {
      final old = shotBarcodeReader;
      addTearDown(() => shotBarcodeReader = old);
      shotBarcodeReader = (_) async => '4700000310326';
      final vision = await pump(tester, mode: 'shelf', ai: const [SeenItem(name: 'не должно вызываться')]);
      List<SeenMatch>? got;
      late WidgetRef r;
      await tester.pumpWidget(
        ProviderScope(
          overrides: [databaseProvider.overrideWithValue(db), apiProvider.overrideWithValue(api), visionProvider.overrideWithValue(vision), sessionProvider.overrideWith(() => _Fixed(_owner))],
          child: MaterialApp(
            theme: buildTheme(dark: false),
            locale: const Locale('ru'),
            supportedLocales: AppLocalizations.supportedLocales,
            localizationsDelegates: const [AppLocalizations.delegate, GlobalMaterialLocalizations.delegate, GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate],
            home: Consumer(
              builder: (c, ref, _) {
                r = ref;
                return Scaffold(
                  body: TextButton(
                    onPressed: () async => got = await recognizeBytes(c, r, Uint8List(50), 'shelf'),
                    child: const Text('go'),
                  ),
                );
              },
            ),
          ),
        ),
      );
      await tester.tap(find.text('go'));
      await settle(tester);
      expect(vision.calls, isEmpty);
      expect(got!.single.picked?.name, 'Адыгене 1л');
      expect(got!.single.candidates.first.$2, 1.0);
    });

    for (final (lang, dark) in [('ky', false), ('uz', true)]) {
      testWidgets('360 dp, $lang, шрифт 1,3${dark ? ', тёмная тема' : ''}: накладная и варианты без переполнения', (tester) async {
        await pump(
          tester,
          mode: 'invoice',
          locale: Locale(lang),
          scale: 1.3,
          dark: dark,
          matches: [
            SeenMatch(const SeenItem(brand: 'ASU', name: 'Still water', size: '1 L', count: 3), [(_p('p2', 'ASU Негаз 1 л клуб-киви'), 0.8), (_p('p3', 'ASU Негаз 1л персик'), 0.78)]),
          ],
        );
        expect(tester.takeException(), isNull);
        for (final k in ['cam_gallery', 'cam_file', 'cand_p2', 'cand_p3']) {
          final f = find.byKey(ValueKey(k)).evaluate().isNotEmpty ? find.byKey(ValueKey(k)) : find.byKey(Key(k));
          expect(f, findsOneWidget, reason: k);
          expect(tester.getSize(f).height, greaterThanOrEqualTo(48), reason: k);
        }
      });
    }
  });
}
