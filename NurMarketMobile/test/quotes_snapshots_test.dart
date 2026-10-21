// 2026-10-21: снимки экранов КП и счетов для сверки дизайна. Без --dart-define=SNAP_DIR=<папка> тест пропускается —
// в репозиторий ничего не пишется. Пример: flutter test test/quotes_snapshots_test.dart --dart-define=SNAP_DIR=C:/tmp/snaps
import 'dart:io';
import 'dart:ui' as ui;

import 'package:drift/native.dart';
import 'package:flutter/material.dart';
import 'package:flutter/rendering.dart';
import 'package:flutter/services.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/providers.dart';
import 'package:nurmarket/core/theme/theme.dart';
import 'package:nurmarket/features/admin/quotes.dart';
import 'package:nurmarket/features/admin/waybill_pdf.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

const _dir = String.fromEnvironment('SNAP_DIR');

Future<void> _fonts() async {
  final m = FontLoader('Montserrat');
  for (final w in ['Regular', 'Medium', 'SemiBold', 'Bold', 'ExtraBold']) {
    m.addFont(rootBundle.load('assets/fonts/Montserrat-$w.ttf'));
  }
  await m.load();
  final root = Platform.environment['FLUTTER_ROOT'] ?? 'D:/flutter';
  final icons = File('$root/bin/cache/artifacts/material_fonts/materialicons-regular.otf');
  if (icons.existsSync()) {
    await (FontLoader('MaterialIcons')..addFont(Future.value(ByteData.sublistView(icons.readAsBytesSync())))).load();
  }
}

void main() {
  final now = DateUtils.dateOnly(DateTime.now());
  List<QuoteDoc> docs() => [
    QuoteDoc(
      id: '1',
      number: 'КП-0012',
      isQuote: true,
      client: 'Бакыт Асанов',
      company: 'ОсОО «Үмүт-Сервис»',
      phone: '0555 777 888',
      inn: '01234567890123',
      lines: [
        QuoteLine(productId: 'a', name: 'Вода «Нур» 1,5 л', qty: 24, price: 62),
        QuoteLine(productId: 'b', name: 'Сок яблочный «Gracio» 1 л, упаковка 12 шт', qty: 2, price: 1450),
        QuoteLine(productId: 'c', name: 'Кофе зерновой «Жаңы күн» 1 кг', unit: 'кг', qty: 1.5, price: 2380),
      ],
      discount: 300,
      until: now.add(const Duration(days: 6)),
      status: 'sent',
      createdAt: now.subtract(const Duration(days: 1)),
      history: ['20.10.2026 14:05 · PDF · Отправлено'],
    ),
    QuoteDoc(
      id: '2',
      number: 'СЧ-0007',
      isQuote: false,
      client: 'Айгүл',
      company: 'ИП Токтосунова',
      phone: '0700 123 456',
      lines: [QuoteLine(productId: 'd', name: 'Мука высший сорт 50 кг', qty: 10, price: 2150)],
      until: now.subtract(const Duration(days: 2)),
      status: 'partial',
      paid: 8000,
      createdAt: now.subtract(const Duration(days: 9)),
      basedOn: 'КП-0009',
    ),
    QuoteDoc(
      id: '3',
      number: 'КП-0010',
      isQuote: true,
      client: 'Нурлан',
      lines: [QuoteLine(productId: 'e', name: 'Сахар 1 кг', qty: 100, price: 95)],
      until: now.subtract(const Duration(days: 3)),
      status: 'sent',
      createdAt: now.subtract(const Duration(days: 14)),
    ),
    QuoteDoc(
      id: '4',
      number: 'СЧ-0006',
      isQuote: false,
      client: 'Эрмек',
      lines: [QuoteLine(productId: 'f', name: 'Масло подсолнечное 5 л', qty: 12, price: 1180)],
      until: now.add(const Duration(days: 1)),
      status: 'paid',
      paid: 14160,
      createdAt: now.subtract(const Duration(days: 20)),
    ),
  ];

  late AppDatabase db;
  setUp(() async {
    db = AppDatabase(NativeDatabase.memory());
    await QuotesStore(db).save(docs());
  });
  tearDown(() => db.close());

  final shot = GlobalKey();
  Widget app(Widget home, {bool dark = false}) => RepaintBoundary(
    key: shot,
    child: ProviderScope(
      overrides: [databaseProvider.overrideWithValue(db)],
      child: MaterialApp(
        debugShowCheckedModeBanner: false,
        theme: buildTheme(dark: false),
        darkTheme: buildTheme(dark: true),
        themeMode: dark ? ThemeMode.dark : ThemeMode.light,
        locale: const Locale('ru'),
        supportedLocales: AppLocalizations.supportedLocales,
        localizationsDelegates: const [AppLocalizations.delegate, GlobalMaterialLocalizations.delegate, GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate],
        home: home,
      ),
    ),
  );

  Future<void> save(WidgetTester t, String name) async {
    await t.runAsync(() async {
      final b = shot.currentContext!.findRenderObject()! as RenderRepaintBoundary;
      final img = await b.toImage(pixelRatio: 2);
      final png = await img.toByteData(format: ui.ImageByteFormat.png);
      File('$_dir/$name.png').writeAsBytesSync(png!.buffer.asUint8List());
    });
  }

  Future<void> phone(WidgetTester t) async {
    await t.runAsync(_fonts);
    t.view.physicalSize = const Size(780, 1688);
    t.view.devicePixelRatio = 2;
    addTearDown(t.view.reset);
  }

  for (final dark in [false, true]) {
    final sfx = dark ? '_dark' : '';
    testWidgets('снимок: список$sfx', (t) async {
      await phone(t);
      await t.pumpWidget(app(const QuotesScreen(), dark: dark));
      await t.pumpAndSettle();
      await save(t, 'after_list$sfx');
      await t.tap(find.text('КП-0012'));
      await t.pumpAndSettle();
      await save(t, 'after_view_quote$sfx');
    }, skip: _dir.isEmpty);
  }

  testWidgets('снимок: счёт с частичной оплатой и просрочкой', (t) async {
    await phone(t);
    await t.pumpWidget(app(const QuotesScreen()));
    await t.pumpAndSettle();
    await t.tap(find.text('СЧ-0007'));
    await t.pumpAndSettle();
    await save(t, 'after_view_invoice');
  }, skip: _dir.isEmpty);

  testWidgets('снимок: пустой список', (t) async {
    await phone(t);
    // 2026-10-21: удаление теперь в несколько шагов (надгробия) — запись базы вне «ненастоящего» времени теста.
    await t.runAsync(() => QuotesStore(db).save([]));
    await t.pumpWidget(app(const QuotesScreen()));
    await t.pumpAndSettle();
    await save(t, 'after_list_empty');
  }, skip: _dir.isEmpty);

  testWidgets('снимок: редактирование и «Новый клиент»', (t) async {
    await phone(t);
    await t.pumpWidget(app(QuoteEditScreen(doc: docs().first, quote: true)));
    await t.pumpAndSettle();
    await save(t, 'after_edit');
    await t.tap(find.text('Новый клиент'));
    await t.pumpAndSettle();
    await save(t, 'after_new_client');
  }, skip: _dir.isEmpty);

  test('PDF КП и счёта в папку снимков', () async {
    TestWidgetsFlutterBinding.ensureInitialized();
    final d = docs();
    final many = docs().first..lines = [for (var i = 0; i < 45; i++) ...docs().first.lines];
    for (final (name, doc, kind, req) in [
      ('kp', d[0], DocKind.quote, ''),
      ('schet', d[1], DocKind.invoice, 'Банк «Бай-Түшүм», р/с 1280016030170412, БИК 128001\nТел. 0555 000 111, Бишкек'),
      ('kp_long', many, DocKind.quote, ''),
    ]) {
      final r = quoteReceipt(doc, shop: 'Нур Маркет', inn: '02345678901234', address: 'Бишкек, ул. Чүй 120', cashier: 'Айбек');
      final bytes = await docPdfBytes(
        r,
        kind,
        basis: kind == DocKind.quote ? 'коммерческое предложение' : 'коммерческое предложение ${doc.basedOn}',
        note: kind == DocKind.quote ? 'Предложение действует до 27.10.2026.' : 'Оплатить до 19.10.2026. В назначении платежа укажите номер счёта.',
        requisites: req,
        paid: doc.isQuote ? 0 : doc.paid,
      );
      File('$_dir/$name.pdf').writeAsBytesSync(bytes);
    }
  }, skip: _dir.isEmpty);
}
