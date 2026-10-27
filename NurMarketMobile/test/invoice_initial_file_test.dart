// 2026-10-27 (владелец: «в накладной добавь выбор файла Excel»): файл, выбранный в «Умной камере», открывается в экране
// «Загрузить накладную» сразу (InvoiceFileArg / initialFile), без повторного выбора. Сети нет: поставщики и справочник подставлены.
import 'dart:io';
import 'dart:typed_data';

import 'package:drift/drift.dart' show Value;
import 'package:drift/native.dart';
import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/catalog/global_products.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/providers.dart';
import 'package:nurmarket/core/theme/theme.dart';
import 'package:nurmarket/core/invoice/invoice_match.dart' show InvoiceGuard;
import 'package:nurmarket/features/admin/invoice_import.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

class _Api extends ApiClient {
  _Api() : super(SessionStore());
  @override
  Future<dynamic> get(String path, {Map<String, dynamic>? query, Duration? timeout}) async => {'purchase_price': '41000'};
}

void main() {
  late AppDatabase db;
  setUp(() async {
    db = AppDatabase(NativeDatabase.memory());
    InvoiceGuard.resetPending();
    await db.upsertProduct(ProductsCompanion.insert(id: 'mon', name: 'Монитор 24" IPS Full HD', price: const Value(14500), quantity: const Value(3), searchKey: const Value('монитор 24" ips full hd')));
  });
  tearDown(() async => db.close());

  Future<void> open(WidgetTester t, InvoiceFileArg f) => t.pumpWidget(
    ProviderScope(
      overrides: [
        databaseProvider.overrideWithValue(db),
        apiProvider.overrideWithValue(_Api()),
        globalProductsProvider.overrideWithValue(GlobalProducts(openFile: () async => throw StateError('справочника в проверке нет'))),
      ],
      child: MaterialApp(
        theme: buildTheme(dark: false),
        locale: const Locale('ru'),
        supportedLocales: AppLocalizations.supportedLocales,
        localizationsDelegates: const [AppLocalizations.delegate, GlobalMaterialLocalizations.delegate, GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate],
        home: InvoiceImportScreen(initialFile: f),
      ),
    ),
  );

  testWidgets('выбранная накладная (xlsx) разбирается без повторного выбора файла', (t) async {
    t.view.physicalSize = const Size(720, 8000);
    t.view.devicePixelRatio = 2;
    addTearDown(t.view.reset);
    final bytes = Uint8List.fromList(File('test/fixtures/invoices/owner_torg12.xlsx').readAsBytesSync());
    await open(t, InvoiceFileArg(bytes, 'owner_torg12.xlsx'));
    final l = lookupAppLocalizations(const Locale('ru'));
    for (var i = 0; i < 200; i++) {
      await t.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 50)));
      await t.pump();
      if (find.textContaining('Строк: 4').evaluate().isNotEmpty) break;
    }
    // ignore: avoid_print
    print(find.byType(Text).evaluate().map((e) => (e.widget as Text).data).join(' | '));
    expect(find.textContaining('Строк: 4'), findsOneWidget, reason: 'те же 4 строки ТОРГ-12, что у debugInvoice');
    expect(find.text(l.invPick), findsNothing, reason: 'кнопка выбора файла уже не нужна');
  });

  testWidgets('плохой файл — понятная ошибка, не падение', (t) async {
    await open(t, InvoiceFileArg(Uint8List.fromList([1, 2, 3, 4]), 'мусор.xlsx'));
    for (var i = 0; i < 40; i++) {
      await t.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 40)));
      await t.pump();
    }
    expect(t.takeException(), isNull);
    expect(find.text(lookupAppLocalizations(const Locale('ru')).invPick), findsOneWidget, reason: 'после ошибки можно выбрать другой файл');
  });
}
