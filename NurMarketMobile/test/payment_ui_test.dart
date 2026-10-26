// 2026-10-26 (живой аудит, П4): оплата, «Оплачено», экран чека — «Останется долга», окно клиента (заголовок, «Закрыть»,
// долг), подписи кнопок чека целиком при шрифте 1,3× на 360 dp, номер чека в одном формате, альбомная ориентация —
// «Провести оплату» достижима прокруткой. Сервер не трогается — запросы идут в фейк.
import 'package:drift/native.dart';
import 'package:flutter/material.dart';
import 'package:flutter/rendering.dart';
import 'package:flutter/services.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/providers.dart';
import 'package:nurmarket/core/theme/theme.dart';
import 'package:nurmarket/features/payment/payment_screen.dart';
import 'package:nurmarket/features/payment/sales_repository.dart';
import 'package:nurmarket/features/receipt/receipt.dart';
import 'package:nurmarket/features/receipt/receipt_view.dart';
import 'package:nurmarket/features/sale/cart.dart';
import 'package:nurmarket/features/shift/shift_repository.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

class _Api extends ApiClient {
  _Api() : super(SessionStore());
  @override
  Future<dynamic> get(String path, {Map<String, dynamic>? query, Duration? timeout}) async {
    if (path == 'api/main/clients/') {
      return {
        'results': [
          {'id': 'c1', 'full_name': 'Асель', 'phone': '0700123456'},
          {'id': 'c2', 'full_name': 'Бакыт', 'phone': '0555000111'},
        ],
      };
    }
    if (path == 'api/main/clients/debtors/') {
      return {
        'results': [
          {'client': 'c1', 'full_name': 'Асель', 'phone': '0700123456', 'debt_total': '394.00', 'sales_count': 2},
        ],
      };
    }
    throw ApiException('Нет связи с сервером.');
  }
}

class _Fixed extends SessionController {
  final Session s;
  _Fixed(this.s);
  @override
  Session? build() => s;
}

class _OpenShift extends ShiftController {
  @override
  ShiftState build() => const ShiftState(shift: ShiftInfo(id: 'sh1'));
}

const _session = Session(email: 'a@x.kg', access: 'a', refresh: 'r', userId: 'u', userName: 'Айгерим Т.', role: 'owner', isOwner: true, companyName: 'Нур', cashboxId: 'b', cashboxName: 'Касса 1');

Cart _cart() => Cart(lines: [CartLine(productId: 'p', name: 'Кофе', price: 430)]);

Future<void> _fonts() async {
  final m = FontLoader('Montserrat');
  for (final w in ['Regular', 'Medium', 'SemiBold', 'Bold', 'ExtraBold']) {
    m.addFont(rootBundle.load('assets/fonts/Montserrat-$w.ttf'));
  }
  await m.load();
}

List<String> _clipped() {
  final out = <String>[];
  for (final e in find.byType(RichText).evaluate()) {
    final r = e.renderObject;
    if (r is RenderParagraph && r.didExceedMaxLines) out.add(r.text.toPlainText());
  }
  return out;
}

void main() {
  late AppDatabase db;
  setUpAll(_fonts);
  setUp(() => db = AppDatabase(NativeDatabase.memory()));
  tearDown(() => db.close());

  void screen(WidgetTester t, {double w = 360, double h = 640, double scale = 1.0}) {
    t.view.physicalSize = Size(w * 2, h * 2);
    t.view.devicePixelRatio = 2.0;
    t.platformDispatcher.textScaleFactorTestValue = scale;
    final prev = FlutterError.onError;
    FlutterError.onError = (d) {
      if (d.exceptionAsString().contains('not supported by all of its localization delegates')) return;
      prev?.call(d);
    };
    addTearDown(() {
      FlutterError.onError = prev;
      t.view.resetPhysicalSize();
      t.view.resetDevicePixelRatio();
      t.platformDispatcher.clearTextScaleFactorTestValue();
    });
  }

  Widget app(Widget home, {String lang = 'ru', bool dark = false}) => ProviderScope(
    overrides: [
      databaseProvider.overrideWithValue(db),
      apiProvider.overrideWithValue(_Api()),
      sessionProvider.overrideWith(() => _Fixed(_session)),
      shiftProvider.overrideWith(_OpenShift.new),
    ],
    child: MaterialApp(
      theme: buildTheme(dark: false),
      darkTheme: buildTheme(dark: true),
      themeMode: dark ? ThemeMode.dark : ThemeMode.light,
      locale: Locale(lang),
      supportedLocales: AppLocalizations.supportedLocales,
      localizationsDelegates: const [AppLocalizations.delegate, GlobalMaterialLocalizations.delegate, GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate],
      home: home,
    ),
  );

  ProviderContainer of(WidgetTester t) => ProviderScope.containerOf(t.element(find.byType(Scaffold).first));

  Future<void> openPayment(WidgetTester t, {String lang = 'ru', bool dark = false}) async {
    await t.pumpWidget(app(const PaymentScreen(), lang: lang, dark: dark));
    of(t).read(cartProvider.notifier).restore(_cart());
    await t.pumpAndSettle();
  }

  testWidgets('в долг: «Останется долга» пересчитывается от внесённой суммы', (t) async {
    screen(t);
    await openPayment(t);
    await t.tap(find.text('В долг'));
    await t.pumpAndSettle();
    await t.drag(find.byType(ListView).first, const Offset(0, -300)); // ниже сгиба строки строятся лениво
    await t.pumpAndSettle();
    expect(find.text('Останется долга: 430 сом'), findsOneWidget);
    await t.enterText(find.byType(TextField).last, '100');
    await t.pumpAndSettle();
    expect(find.text('Останется долга: 330 сом'), findsOneWidget);
    await t.enterText(find.byType(TextField).last, '430');
    await t.pumpAndSettle();
    expect(find.text('Останется долга: 0 сом'), findsOneWidget);
  });

  testWidgets('окно клиента: заголовок «Клиент», «Закрыть», долг у должника; выбор переносит долг в строку клиента', (t) async {
    screen(t);
    await openPayment(t);
    await t.tap(find.text('Выбрать клиента'));
    await t.pumpAndSettle();
    expect(find.text('Клиент'), findsWidgets);
    expect(find.text('Закрыть'), findsOneWidget);
    expect(find.text('Долг: 394 сом'), findsOneWidget, reason: 'долг только у должника');
    expect(find.text('Бакыт'), findsOneWidget);
    // «Закрыть» закрывает окно, ничего не выбирая
    await t.tap(find.text('Закрыть'));
    await t.pumpAndSettle();
    expect(find.text('Закрыть'), findsNothing);
    expect(find.text('Выбрать клиента'), findsOneWidget);

    await t.tap(find.text('Выбрать клиента'));
    await t.pumpAndSettle();
    await t.tap(find.text('Асель'));
    await t.pumpAndSettle();
    expect(find.textContaining('Асель'), findsOneWidget);
    expect(find.text('Долг: 394 сом'), findsOneWidget);
  });

  testWidgets('окно клиента не уходит под строку состояния при клавиатуре', (t) async {
    screen(t);
    await openPayment(t);
    await t.tap(find.text('Выбрать клиента'));
    await t.pumpAndSettle();
    t.view.padding = const FakeViewPadding(top: 48 * 2); // строка состояния 24 dp ×2
    t.view.viewInsets = const FakeViewPadding(bottom: 280 * 2); // клавиатура открылась
    addTearDown(t.view.resetPadding);
    addTearDown(t.view.resetViewInsets);
    await t.pumpAndSettle();
    expect(t.takeException(), isNull);
    final top = t.getTopLeft(find.text('Закрыть')).dy;
    expect(top, greaterThanOrEqualTo(48), reason: 'заголовок ниже строки состояния');
  });

  for (final lang in ['ru', 'ky']) {
    testWidgets('альбомная ориентация 700×340: кнопка в конце прокрутки, без переполнения — $lang', (t) async {
      screen(t, w: 700, h: 340);
      await openPayment(t, lang: lang);
      expect(t.takeException(), isNull);
      final scaffold = t.widget<Scaffold>(find.byType(Scaffold).first);
      expect(scaffold.bottomNavigationBar, isNull, reason: 'кнопка не закреплена');
      await t.drag(find.byType(ListView).first, const Offset(0, -3000));
      await t.pumpAndSettle();
      final btn = find.byType(FilledButton);
      expect(btn, findsOneWidget);
      expect(t.getBottomLeft(btn).dy, lessThanOrEqualTo(340));
      expect(t.takeException(), isNull);
    });
  }

  testWidgets('портрет: кнопка оплаты закреплена внизу', (t) async {
    screen(t);
    await openPayment(t);
    expect(t.widget<Scaffold>(find.byType(Scaffold).first).bottomNavigationBar, isNotNull);
  });

  for (final scale in [1.0, 1.3]) {
    for (final lang in ['ru', 'ky']) {
      testWidgets('«Оплачено» на 360×640: подписи целиком, номер с нулями — $lang, шрифт $scale', (t) async {
        screen(t, scale: scale);
        final receipt = Receipt.fromCart(session: _session, cart: _cart(), number: '1601', payment: 'cash', received: 500);
        final info = PaidInfo(CheckoutResult(queued: false, key: 'k', number: '1601', total: 430), 70, receipt);
        await t.pumpWidget(app(PaidScreen(info: info), lang: lang));
        await t.pumpAndSettle();
        expect(t.takeException(), isNull);
        expect(_clipped(), isEmpty, reason: 'обрезано: ${_clipped()}');
        expect(find.textContaining('№001601'), findsOneWidget);
        expect(find.textContaining('№1601'), findsNothing);
        expect(find.text('QR'), findsOneWidget);
        expect(find.text('PDF'), findsOneWidget);
        // подпись «PDF» — одна строка (не переносится посреди слова)
        for (final w in ['QR', 'PDF']) {
          expect(t.getSize(find.text(w)).height, lessThan(30 * scale));
        }
      });
    }
  }

  testWidgets('«Оплачено» в альбомной ориентации прокручивается, ничего не переполняет', (t) async {
    screen(t, w: 700, h: 340);
    final receipt = Receipt.fromCart(session: _session, cart: _cart(), number: '1601', payment: 'cash', received: 500);
    final info = PaidInfo(CheckoutResult(queued: false, key: 'k', number: '1601', total: 430), 70, receipt);
    await t.pumpWidget(app(PaidScreen(info: info)));
    await t.pumpAndSettle();
    expect(t.takeException(), isNull);
    await t.drag(find.byType(CustomScrollView), const Offset(0, -3000));
    await t.pumpAndSettle();
    expect(find.text('Новый чек'), findsOneWidget);
    expect(t.getBottomLeft(find.text('Новый чек')).dy, lessThanOrEqualTo(340));
  });

  testWidgets('экран чека: «Печать», «QR», «PDF» в одну строку при шрифте 1,3× на 360 dp', (t) async {
    screen(t, scale: 1.3);
    final receipt = Receipt.fromCart(session: _session, cart: _cart(), number: '1601', payment: 'cash', received: 500);
    await t.pumpWidget(app(ReceiptScreen(receipt: receipt), lang: 'ky'));
    await t.pumpAndSettle();
    expect(t.takeException(), isNull);
    expect(_clipped(), isEmpty);
    expect(find.text('QR'), findsOneWidget);
    expect(find.text('PDF'), findsOneWidget);
  });

  test('чек с сервера: кассир из cashier_display, касса из cashbox_name', () {
    final r = Receipt.fromSale(
      session: _session,
      sale: {'cashier_display': 'Нурлан К.', 'cashbox_name': 'Касса 2', 'items': <dynamic>[]},
      number: '1601',
      total: 430,
      at: DateTime(2026, 10, 26, 12),
      payment: 'cash',
    );
    expect(r.cashier, 'Нурлан К.');
    expect(r.cashbox, 'Касса 2');
    expect(r.numberText, '001601');
  });
}
