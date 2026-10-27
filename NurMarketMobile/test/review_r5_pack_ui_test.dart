// 2026-10-27 (поток Р-5, ревью М-5 / Ск-11): накладная в коробках. Строка «кор» к товару, который ведётся в штуках, раньше молча
// зачисляла коробки как штуки по цене коробки (+2 шт вместо +48, закупка 600 за штуку, авто-цена 780). Теперь кассир ЯВНО выбирает:
// «пересчитать в штуки» (количество × штук в упаковке, закупка ÷ штук) или «принять как есть»; пока не выбрал — приход не добавляется.
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

class _Api extends ApiClient {
  _Api() : super(SessionStore());
  @override
  Future<dynamic> get(String path, {Map<String, dynamic>? query, Duration? timeout}) async => {'purchase_price': '20'};
}

ParsedInvoice _inv({String unit = 'кор', String barcode = '', double qty = 2, double cost = 600}) => ParsedInvoice(
  format: InvoiceFormat.xlsx,
  number: '77',
  date: DateTime(2026, 10, 27),
  lines: [InvoiceLine(row: 1, name: 'Кола 0,5', barcode: barcode, unit: unit, qty: qty, cost: cost, sum: qty * cost)],
  fileTotal: qty * cost,
  fileHash: 'hh',
);

void main() {
  late AppDatabase db;
  final l = lookupAppLocalizations(const Locale('ru'));

  Future<void> add(String id, String name, {String unit = 'шт', double price = 30, String alt = ''}) => db.upsertProduct(
    ProductsCompanion.insert(id: id, name: name, unit: Value(unit), price: Value(price), quantity: const Value(5), altBarcodes: Value(alt), searchKey: Value(name.toLowerCase())),
  );

  setUp(() async {
    db = AppDatabase(NativeDatabase.memory());
    InvoiceGuard.resetPending();
  });
  tearDown(() => db.close());

  late ReceiveHandoff? got;

  Future<void> open(WidgetTester t, ParsedInvoice inv) async {
    t.view.physicalSize = const Size(720, 3200);
    t.view.devicePixelRatio = 2.0;
    addTearDown(t.view.resetPhysicalSize);
    addTearDown(t.view.resetDevicePixelRatio);
    got = null;
    final router = GoRouter(
      routes: [
        GoRoute(
          path: '/',
          builder: (c, _) => Scaffold(
            body: Center(
              child: ElevatedButton(
                key: const Key('go'),
                onPressed: () async => got = await c.push<ReceiveHandoff>('/admin/invoice'),
                child: const Text('go'),
              ),
            ),
          ),
        ),
        GoRoute(path: '/admin/invoice', builder: (_, _) => InvoiceImportScreen(forReceive: true, debugInvoice: inv, debugSuppliers: const [])),
      ],
    );
    await t.pumpWidget(
      ProviderScope(
        overrides: [
          databaseProvider.overrideWithValue(db),
          apiProvider.overrideWithValue(_Api()),
          globalProductsProvider.overrideWithValue(GlobalProducts(openFile: () async => throw StateError('нет'))),
        ],
        child: MaterialApp.router(
          routerConfig: router,
          theme: buildTheme(dark: false),
          locale: const Locale('ru'),
          supportedLocales: AppLocalizations.supportedLocales,
          localizationsDelegates: const [AppLocalizations.delegate, GlobalMaterialLocalizations.delegate, GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate],
        ),
      ),
    );
    await t.tap(find.byKey(const Key('go')));
    for (var i = 0; i < 30; i++) {
      await t.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 40)));
      await t.pump();
    }
  }

  FilledButton addButton(WidgetTester t) => t.widget<FilledButton>(find.byType(FilledButton).last);

  testWidgets('«кор» к товару в штуках: предупреждение, кнопка «Добавить» выключена, пока кассир не выбрал', (t) async {
    await add('c05', 'Кола 0,5');
    await open(t, _inv());
    expect(find.text(l.invPackWarn('кор', 'Кола 0,5', 'шт')), findsOneWidget);
    expect(find.byKey(const ValueKey('packPieces_1')), findsOneWidget);
    expect(find.byKey(const ValueKey('packAsIs_1')), findsOneWidget);
    expect(addButton(t).onPressed, isNull, reason: 'молча принять коробки как штуки нельзя');
  });

  testWidgets('«Пересчитать в штуки» 24: 2 кор × 600 -> 48 шт по 25, итог тот же; в приход уходит 48 шт', (t) async {
    await add('c05', 'Кола 0,5');
    await open(t, _inv());
    await t.enterText(find.byKey(const ValueKey('packN_1')), '24');
    await t.tap(find.byKey(const ValueKey('packPieces_1')));
    await t.pump();
    expect(find.text(l.invPackDonePieces(24, '48', money(25))), findsOneWidget);
    expect(addButton(t).onPressed, isNotNull);
    await t.tap(find.byType(FilledButton).last);
    for (var i = 0; i < 10; i++) {
      await t.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 40)));
      await t.pump();
    }
    expect(got, isNotNull);
    final line = got!.lines.single;
    expect(line.productId, 'c05');
    expect(line.qty, 48);
    expect(line.cost, 25);
    expect(line.qty * line.cost, 1200, reason: 'сумма накладной сохранена');
  });

  testWidgets('«Принять как есть»: явный выбор, коробки идут как есть (2 × 600) и это написано', (t) async {
    await add('c05', 'Кола 0,5');
    await open(t, _inv());
    await t.tap(find.byKey(const ValueKey('packAsIs_1')));
    await t.pump();
    expect(find.textContaining('Принято как есть'), findsOneWidget);
    expect(addButton(t).onPressed, isNotNull);
    await t.tap(find.byKey(const ValueKey('packReset_1')));
    await t.pump();
    expect(find.byKey(const ValueKey('packPieces_1')), findsOneWidget, reason: '«Изменить» возвращает выбор');
    expect(addButton(t).onPressed, isNull);
  });

  testWidgets('количество штук в упаковке подставляется из доп. штрихкода коробки (код|24)', (t) async {
    await add('c05', 'Кола 0,5', alt: '4600000000017|24');
    await open(t, _inv(unit: 'шт', barcode: '4600000000017'));
    final field = t.widget<TextField>(find.byKey(const ValueKey('packN_1')));
    expect(field.controller!.text, '24', reason: 'код коробки с множителем: единица «шт», но код упаковочный — всё равно спрашиваем');
    expect(addButton(t).onPressed, isNull);
  });

  testWidgets('штучная строка и товар в упаковках (unit упак) — вопроса нет', (t) async {
    await add('c05', 'Кола 0,5');
    await open(t, _inv(unit: 'шт', qty: 5, cost: 25));
    expect(find.byKey(const ValueKey('packPieces_1')), findsNothing);
    expect(addButton(t).onPressed, isNotNull);
  });

  testWidgets('число штук в упаковке неверное (1, пусто) — не пересчитывает, пишет подсказку', (t) async {
    await add('c05', 'Кола 0,5');
    await open(t, _inv());
    await t.enterText(find.byKey(const ValueKey('packN_1')), '1');
    await t.tap(find.byKey(const ValueKey('packPieces_1')));
    await t.pump();
    expect(find.text(l.invPackBad), findsOneWidget);
    expect(addButton(t).onPressed, isNull);
    await t.pumpAndSettle(const Duration(seconds: 5));
  });

  testWidgets('Ск-14: накладная в рублях — «Добавить» выключена, пока кассир не поставит галочку про валюту', (t) async {
    await add('c05', 'Кола 0,5');
    final inv = ParsedInvoice(
      format: InvoiceFormat.xlsx,
      number: '5',
      lines: const [InvoiceLine(row: 1, name: 'Кола 0,5', unit: 'шт', qty: 2, cost: 25, sum: 50)],
      currency: 'RUB',
      fileTotal: 50,
      fileHash: 'rub',
    );
    await open(t, inv);
    expect(find.text(l.invCurrencyOk('RUB')), findsOneWidget);
    expect(addButton(t).onPressed, isNull);
    await t.tap(find.byKey(const Key('curOk')));
    await t.pump();
    expect(addButton(t).onPressed, isNotNull);
  });

  testWidgets('Ск-11: единица накладной «кг», а у товара «шт» — «Проверьте · единица»; одинаковые — нет', (t) async {
    await add('c05', 'Кола 0,5');
    await open(t, _inv(unit: 'кг', qty: 5, cost: 25));
    expect(find.text(l.invUnitCheck('кг', 'шт')), findsOneWidget);
    await open(t, _inv(unit: 'шт', qty: 5, cost: 25));
    expect(find.textContaining('Проверьте · единица'), findsNothing);
  });

  testWidgets('Ск-12: в файле только цены с НДС — предупреждение над списком', (t) async {
    await add('c05', 'Кола 0,5');
    final inv = ParsedInvoice(
      format: InvoiceFormat.xlsx,
      number: '6',
      lines: const [InvoiceLine(row: 1, name: 'Кола 0,5', unit: 'шт', qty: 2, cost: 100, costGross: 112, sum: 0, sumGross: 224, vat: 12, costFromGross: true)],
      fileHash: 'g',
    );
    await open(t, inv);
    expect(find.text(l.invGrossOnly), findsOneWidget);
  });
}
