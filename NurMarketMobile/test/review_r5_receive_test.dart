// 2026-10-27 (поток Р-5, ревью Ск-2 / Ск-5 / Ск-6 мобилки): накладная в открытом приходе — закупка уже введённых штук не затирается
// (средневзвешенная), поставщик прихода берётся из явного выбора (вопрос при расхождении, две разные накладные в один приход нельзя),
// повторная накладная ловится ещё раз перед проведением, накладная отмечается «загружена» при первом проведённом пункте.
import 'dart:io';

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
import 'package:nurmarket/core/providers.dart';
import 'package:nurmarket/core/theme/theme.dart';
import 'package:nurmarket/core/util/audit.dart';
import 'package:nurmarket/features/admin/admin_receive.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

class _Api extends ApiClient {
  _Api() : super(SessionStore());
  final posts = <String>[];
  @override
  Future<dynamic> get(String path, {Map<String, dynamic>? query, Duration? timeout}) async {
    if (path == 'api/main/clients/') {
      return {
        'results': [
          {'id': 's1', 'full_name': 'Поставщик Один'},
          {'id': 's2', 'full_name': 'Поставщик Два'},
        ],
      };
    }
    return {'purchase_price': '100', 'quantity': 0};
  }

  @override
  Future<dynamic> post(String path, {Object? body, Map<String, String>? headers, Duration? timeout}) async {
    posts.add(path);
    if (path.contains('products')) throw ApiException('Товар не создан', status: 400);
    return {'id': 'r1'};
  }

  @override
  Future<dynamic> patch(String path, {Object? body}) async => {};
}

void main() {
  setUpAll(() => Audit.testDirectory = Directory.systemTemp.createTempSync('nm_r5recv_'));
  final l = lookupAppLocalizations(const Locale('ru'));
  late AppDatabase db;
  late _Api api;
  late ReceiveHandoff handoff;
  var posted = 0;

  Future<void> settle(WidgetTester t) async {
    for (var i = 0; i < 8; i++) {
      await t.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 25)));
      await t.pump();
    }
  }

  Future<void> open(WidgetTester t, {List<ReceiveLine>? lines, String? supplier, List<String> keys = const [], VoidCallback? onPosted}) async {
    t.view.physicalSize = const Size(720, 2000);
    t.view.devicePixelRatio = 2.0;
    addTearDown(t.view.resetPhysicalSize);
    addTearDown(t.view.resetDevicePixelRatio);
    db = AppDatabase(NativeDatabase.memory());
    addTearDown(db.close);
    api = _Api();
    posted = 0;
    InvoiceGuard.resetPending();
    final router = GoRouter(
      routes: [
        GoRoute(
          path: '/',
          builder: (_, _) => AdminReceiveScreen(initialLines: lines, initialSupplierId: supplier, initialInvoiceKeys: keys, onPosted: onPosted),
        ),
        GoRoute(
          path: '/admin/invoice',
          builder: (c, _) => Scaffold(
            body: Center(child: ElevatedButton(key: const Key('give'), onPressed: () => c.pop(handoff), child: const Text('отдать накладную'))),
          ),
        ),
      ],
    );
    await t.pumpWidget(
      ProviderScope(
        overrides: [
          databaseProvider.overrideWithValue(db),
          apiProvider.overrideWithValue(api),
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
    await settle(t);
  }

  Future<void> importInvoice(WidgetTester t) async {
    await t.tap(find.text(l.invTitle));
    await settle(t);
    await t.tap(find.byKey(const Key('give')));
    await settle(t);
  }

  testWidgets('Ск-6: закупка введённых штук не затирается — средневзвешенная и сообщение (10 × 50 + 5 × 60 = 15 × 53,33)', (t) async {
    await open(t, lines: [ReceiveLine(productId: 'p1', name: 'Монитор', qty: 10, cost: 50, price: 80, oldPrice: 80)], supplier: 's1');
    handoff = ReceiveHandoff([ReceiveLine(productId: 'p1', name: 'Монитор', qty: 5, cost: 60, price: 80, oldPrice: 80)], supplierId: 's1', supplierName: 'Поставщик Один');
    await importInvoice(t);
    expect(find.text(l.recvCostAvg(1)), findsOneWidget);
    final cost = t.widgetList<TextField>(find.byType(TextField)).map((f) => f.controller?.text ?? '').toList();
    expect(cost.any((s) => s.replaceAll(',', '.') == '53.33'), isTrue, reason: 'закупка 53,33, а не 60: $cost');
    expect(find.text('15'), findsWidgets, reason: 'количество 10 + 5');
  });

  testWidgets('Ск-6: одинаковая закупка — сообщения нет, цена прежняя', (t) async {
    await open(t, lines: [ReceiveLine(productId: 'p1', name: 'Монитор', qty: 10, cost: 50, price: 80, oldPrice: 80)], supplier: 's1');
    handoff = ReceiveHandoff([ReceiveLine(productId: 'p1', name: 'Монитор', qty: 5, cost: 50, price: 80, oldPrice: 80)], supplierId: 's1');
    await importInvoice(t);
    expect(find.text(l.recvCostAvg(1)), findsNothing);
  });

  testWidgets('Ск-5: в приходе выбран «Поставщик Один», накладная от «Поставщик Два» — спрашиваем; выбор «из накладной» ставит второго', (t) async {
    await open(t, lines: [ReceiveLine(productId: 'p1', name: 'Монитор', qty: 1, cost: 50, price: 80, oldPrice: 80)], supplier: 's1');
    handoff = ReceiveHandoff([ReceiveLine(productId: 'p2', name: 'Мышь', qty: 2, cost: 10, price: 20, oldPrice: 20)], supplierId: 's2', supplierName: 'Поставщик Два');
    await importInvoice(t);
    expect(find.text(l.invSupConflictTitle), findsOneWidget);
    expect(find.text(l.invSupConflictBody('Поставщик Два', 'Поставщик Один')), findsOneWidget);
    await t.tap(find.text(l.invSupConflictDoc('Поставщик Два')));
    await settle(t);
    expect(find.text('Поставщик Два'), findsWidgets);
    expect(find.text('Мышь'), findsOneWidget);
  });

  testWidgets('Ск-5: выбор «оставить» сохраняет прежнего поставщика; накладная «без поставщика» тоже не уходит молча прежнему', (t) async {
    await open(t, lines: [ReceiveLine(productId: 'p1', name: 'Монитор', qty: 1, cost: 50, price: 80, oldPrice: 80)], supplier: 's1');
    handoff = ReceiveHandoff([ReceiveLine(productId: 'p2', name: 'Мышь', qty: 2, cost: 10, price: 20, oldPrice: 20)]);
    await importInvoice(t);
    expect(find.text(l.invSupConflictBody(l.noSupplier, 'Поставщик Один')), findsOneWidget, reason: 'накладная без поставщика, в приходе выбран «Один»: вопрос');
    await t.tap(find.text(l.invSupConflictKeep('Поставщик Один')));
    await settle(t);
    expect(find.text('Поставщик Один'), findsWidgets);
  });

  testWidgets('Ск-5: в приходе уже накладная одного поставщика — накладную другого не добавляем (долг не смешивается), отметка «в приходе» снимается', (t) async {
    await open(t, lines: [ReceiveLine(productId: 'p1', name: 'Монитор', qty: 1, cost: 50, price: 80, oldPrice: 80)], supplier: 's1', keys: ['k1'], onPosted: () {});
    handoff = ReceiveHandoff([ReceiveLine(productId: 'p2', name: 'Мышь', qty: 2, cost: 10, price: 20, oldPrice: 20)], supplierId: 's2', invoiceKeys: ['k2']);
    InvoiceGuard.markPending(['k2']);
    await importInvoice(t);
    expect(find.text(l.invSupOtherDoc), findsOneWidget);
    expect(find.text('Мышь'), findsNothing);
    expect(await InvoiceGuard.status(db, ['k2']), isNull, reason: 'накладная не добавлена — «в приходе» снято');
  });

  testWidgets('Ск-2: накладную уже провели (кассой или телефоном) — перед проведением предупреждение; «Отмена» ничего не проводит', (t) async {
    await open(t, lines: [ReceiveLine(productId: 'p1', name: 'Монитор', qty: 1, cost: 50, price: 80, oldPrice: 80)], supplier: 's1', keys: ['kk1'], onPosted: () => posted++);
    await InvoiceGuard.markPosted(db, ['kk1'], number: '7');
    await t.tap(find.textContaining(l.postReceipt).last);
    await t.pump();
    await t.tap(find.widgetWithText(FilledButton, l.postReceipt));
    await settle(t);
    expect(find.text(l.invDupPostedNow), findsOneWidget);
    await t.tap(find.text(l.cancel));
    await settle(t);
    expect(api.posts, isEmpty);
    expect(posted, 0);
  });

  testWidgets('Ск-2: частичное проведение — накладная отмечается «загружена» уже при первой принятой строке (раньше только при полном)', (t) async {
    await open(
      t,
      lines: [
        ReceiveLine(productId: 'p1', name: 'Монитор', qty: 1, cost: 50, price: 80, oldPrice: 80),
        ReceiveLine(productId: '', name: 'Новинка', qty: 1, cost: 10, price: 20, isNew: true), // создание отклоняется (400)
      ],
      supplier: 's1',
      keys: ['kk2'],
      onPosted: () => posted++,
    );
    await t.tap(find.textContaining(l.postReceipt).last);
    await t.pump();
    await t.tap(find.widgetWithText(FilledButton, l.postReceipt));
    await settle(t);
    expect(api.posts.where((p) => p.contains('suppliers/s1/receipt')).length, 1, reason: 'принятая строка проведена');
    expect(posted, 1, reason: 'отметка «загружена» поставлена, хотя одна строка осталась непроведённой');
    expect(find.text('Новинка'), findsOneWidget, reason: 'непринятая строка осталась на экране');
    await t.pumpAndSettle(const Duration(seconds: 5));
  });
}
