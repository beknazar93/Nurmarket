// 2026-10-26 (живой аудит, П2): главная и профиль — подписи не обрезаются многоточием (ru/ky, 360 dp, шрифт 1,0 и 1,3,
// светлая и тёмная тема), «Управление» видно без прокрутки, «Сообщите администратору» только кассиру, заглушки нет.
// Сервер не трогается — запросы идут в фейк.
import 'dart:async';

import 'package:drift/drift.dart' show Value;
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
import 'package:nurmarket/features/home/home_screen.dart';
import 'package:nurmarket/features/profile/profile_screen.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

class _Offline extends ApiClient {
  _Offline() : super(SessionStore());
  @override
  Future<dynamic> get(String path, {Map<String, dynamic>? query, Duration? timeout}) async => throw ApiException('Нет связи с сервером.');
  @override
  Future<dynamic> post(String path, {Object? body, Map<String, String>? headers, Duration? timeout}) async => throw ApiException('Нет связи с сервером.');
}

class _Fixed extends SessionController {
  final Session s;
  _Fixed(this.s);
  @override
  Session? build() => s;
}

const _cashier = Session(email: 'k@x.kg', access: 'a', refresh: 'r', userId: 'u1', userName: 'Айгерим Т.', role: 'cashier', companyName: 'Нур', cashboxId: 'b', cashboxName: 'Касса 1', perms: []);
const _owner = Session(email: 'o@x.kg', access: 'a', refresh: 'r', userId: 'u2', userName: 'Бакыт О.', role: 'owner', isOwner: true, companyName: 'Нур', cashboxId: 'b', cashboxName: 'Касса 1');

void _phone(WidgetTester t, {double scale = 1.0}) {
  t.view.physicalSize = const Size(720, 1280); // 360×640 dp
  t.view.devicePixelRatio = 2.0;
  t.platformDispatcher.textScaleFactorTestValue = scale;
  final prev = FlutterError.onError;
  // кыргызского нет в Material/Cupertino — Flutter только предупреждает (так же и в приложении)
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

/// Все абзацы на экране, которые не поместились в maxLines (то есть обрезаны многоточием).
List<String> _clipped(WidgetTester t) {
  final out = <String>[];
  for (final e in find.byType(RichText).evaluate()) {
    final r = e.renderObject;
    if (r is RenderParagraph && r.didExceedMaxLines) out.add(r.text.toPlainText());
  }
  return out;
}

/// Настоящий шрифт приложения: тестовый Ahem шире Montserrat и «обрезал» бы то, что на телефоне помещается.
Future<void> _fonts() async {
  final m = FontLoader('Montserrat');
  for (final w in ['Regular', 'Medium', 'SemiBold', 'Bold', 'ExtraBold']) {
    m.addFont(rootBundle.load('assets/fonts/Montserrat-$w.ttf'));
  }
  await m.load();
}

void main() {
  late AppDatabase db;
  setUpAll(_fonts);
  setUp(() async {
    db = AppDatabase(NativeDatabase.memory());
    await db.replaceProducts([
      ProductsCompanion.insert(id: 'a', name: 'Вода', price: const Value(85), quantity: const Value(3), searchKey: const Value('вода')),
    ]);
  });
  tearDown(() => db.close());

  Widget app(Widget home, Session s, {String lang = 'ru', bool dark = false}) => ProviderScope(
    overrides: [databaseProvider.overrideWithValue(db), pendingCountProvider.overrideWith((ref) => Stream.value(0)), apiProvider.overrideWithValue(_Offline()), sessionProvider.overrideWith(() => _Fixed(s))],
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

  for (final lang in ['ru', 'ky']) {
    for (final scale in [1.0, 1.3]) {
      for (final dark in [false, true]) {
        testWidgets('главная: без переполнения и без обрезанных подписей — $lang, шрифт $scale, тёмная=$dark', (t) async {
          _phone(t, scale: scale);
          await t.pumpWidget(app(const HomeScreen(), _owner, lang: lang, dark: dark));
          await t.pumpAndSettle();
          expect(t.takeException(), isNull);
          expect(_clipped(t), isEmpty, reason: 'обрезаны многоточием: ${_clipped(t)}');
          // прокрутка до конца — нижние кнопки и плашка «Заканчиваются» тоже без обрезки
          await t.drag(find.byType(ListView).first, const Offset(0, -2000));
          await t.pumpAndSettle();
          expect(t.takeException(), isNull);
          expect(_clipped(t), isEmpty, reason: 'обрезаны многоточием (низ): ${_clipped(t)}');
        });
      }
    }
  }

  testWidgets('владельцу «Управление» видно без прокрутки; «Сообщите администратору» ему не показывается', (t) async {
    _phone(t);
    await t.pumpWidget(app(const HomeScreen(), _owner));
    await t.pumpAndSettle();
    final y = t.getTopLeft(find.text('Управление')).dy;
    expect(y, lessThan(640), reason: '«Управление» должно быть в первом экране');
    expect(y, lessThan(t.getTopLeft(find.text('Продажа')).dy), reason: 'выше плиток, сразу под карточкой смены');
    await t.drag(find.byType(ListView).first, const Offset(0, -2000));
    await t.pumpAndSettle();
    expect(find.text('Сообщите администратору'), findsNothing);
    expect(find.text('Заказ поставщику'), findsOneWidget);
  });

  testWidgets('кассиру: «Управления» нет, «Сообщите администратору» есть', (t) async {
    _phone(t);
    await t.pumpWidget(app(const HomeScreen(), _cashier));
    await t.pumpAndSettle();
    expect(find.text('Управление'), findsNothing);
    await t.drag(find.byType(ListView).first, const Offset(0, -2000));
    await t.pumpAndSettle();
    expect(find.text('Сообщите администратору'), findsOneWidget);
  });

  testWidgets('в шапке главной есть значок «Профиль» (⚙) не меньше 48 dp', (t) async {
    _phone(t);
    await t.pumpWidget(app(const HomeScreen(), _cashier));
    await t.pumpAndSettle();
    final gear = find.byTooltip('Профиль');
    expect(gear, findsOneWidget);
    final size = t.getSize(gear);
    expect(size.width, greaterThanOrEqualTo(48));
    expect(size.height, greaterThanOrEqualTo(48));
  });

  testWidgets('открытие смены: у поля суммы есть подпись «Сумма в кассе на начало смены»', (t) async {
    _phone(t);
    await t.pumpWidget(app(const HomeScreen(), _cashier));
    await t.pumpAndSettle();
    await t.tap(find.widgetWithText(FilledButton, 'Открыть смену'));
    await t.pumpAndSettle();
    expect(find.text('Сумма в кассе на начало смены'), findsOneWidget);
    expect(find.text('сом'), findsWidgets);
  });

  for (final lang in ['ru', 'ky']) {
    testWidgets('профиль: без заглушки «Связь с кассой», без переполнения при 1,3 — $lang', (t) async {
      _phone(t, scale: 1.3);
      await t.pumpWidget(app(const ProfileScreen(), _cashier, lang: lang, dark: true));
      await t.pumpAndSettle();
      expect(t.takeException(), isNull);
      expect(find.text('Связь с кассой'), findsNothing);
      expect(find.text('Байланыш кассалар менен'), findsNothing);
    });
  }
}
