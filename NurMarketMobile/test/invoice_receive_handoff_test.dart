import 'dart:io';

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
import 'package:nurmarket/core/util/audit.dart';
import 'package:nurmarket/features/admin/admin_receive.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

// 2026-10-27: строки из «Загрузить накладную» приходят в обычный экран прихода с поставщиком; проведение — прежним путём
// (suppliers/{id}/receipt/), по полному проведению срабатывает onPosted (накладная записывается как «загружена»).
class _Api extends ApiClient {
  _Api() : super(SessionStore());
  final posts = <String>[];
  final bodies = <Object?>[];
  @override
  Future<dynamic> get(String path, {Map<String, dynamic>? query, Duration? timeout}) async {
    if (path == 'api/main/clients/') {
      return {
        'results': [
          {'id': 's1', 'full_name': 'ООО ТехноСфера-М'},
        ],
      };
    }
    return {'purchase_price': '100', 'quantity': 0};
  }

  @override
  Future<dynamic> post(String path, {Object? body, Map<String, String>? headers, Duration? timeout}) async {
    posts.add(path);
    bodies.add(body);
    return {'id': path.contains('create') ? 'new1' : 'r1'};
  }

  @override
  Future<dynamic> patch(String path, {Object? body}) async => {};
}

void main() {
  setUpAll(() => Audit.testDirectory = Directory.systemTemp.createTempSync('nm_invoice_'));
  testWidgets('приход открыт с поставщиком из накладной; «Провести» → приход поставщику и onPosted', (t) async {
    t.view.physicalSize = const Size(720, 1600);
    t.view.devicePixelRatio = 2.0;
    addTearDown(t.view.resetPhysicalSize);
    addTearDown(t.view.resetDevicePixelRatio);
    final db = AppDatabase(NativeDatabase.memory());
    addTearDown(db.close);
    final api = _Api();
    var posted = 0;
    await t.pumpWidget(
      ProviderScope(
        overrides: [
          databaseProvider.overrideWithValue(db),
          apiProvider.overrideWithValue(api),
          globalProductsProvider.overrideWithValue(GlobalProducts(openFile: () async => throw StateError('нет'))),
        ],
        child: MaterialApp(
          theme: buildTheme(dark: false),
          locale: const Locale('ru'),
          supportedLocales: AppLocalizations.supportedLocales,
          localizationsDelegates: const [AppLocalizations.delegate, GlobalMaterialLocalizations.delegate, GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate],
          home: AdminReceiveScreen(
            initialLines: [
              ReceiveLine(productId: 'p1', name: 'Монитор', qty: 5, cost: 12000, price: 14500, oldPrice: 14500, oldCost: 11000),
            ],
            initialSupplierId: 's1',
            onPosted: () => posted++,
          ),
        ),
      ),
    );
    for (var i = 0; i < 10; i++) {
      await t.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 30)));
      await t.pump();
    }
    final l = lookupAppLocalizations(const Locale('ru'));
    expect(find.text('ООО ТехноСфера-М'), findsOneWidget, reason: 'поставщик из накладной выбран');
    expect(find.text(l.invTitle), findsOneWidget, reason: 'кнопка «Загрузить накладную» на экране прихода');
    expect(find.text('Монитор'), findsOneWidget);
    await t.tap(find.textContaining(l.postReceipt).last);
    await t.pump();
    await t.tap(find.widgetWithText(FilledButton, l.postReceipt));
    for (var i = 0; i < 10; i++) {
      await t.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 30)));
      await t.pump();
    }
    expect(api.posts, contains('api/main/suppliers/s1/receipt/'), reason: 'проведение — прежним путём прихода поставщику');
    await t.pumpAndSettle(const Duration(seconds: 5));
    expect(posted, 1, reason: 'накладная отмечена как загруженная только после проведения');
  });
}
