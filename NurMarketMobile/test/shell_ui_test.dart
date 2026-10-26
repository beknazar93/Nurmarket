// 2026-10-26 (живой аудит, П1): общие виджеты и оболочка — шапка и нижняя панель при шрифте 1,3×, «Назад» на вкладке,
// касание 48 dp, контраст цветов в обеих темах.
import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';
import 'package:nurmarket/core/theme/theme.dart';
import 'package:nurmarket/core/theme/tokens.dart';
import 'package:nurmarket/core/util/ui.dart';
import 'package:nurmarket/features/home/shell.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

const _delegates = <LocalizationsDelegate<dynamic>>[
  AppLocalizations.delegate,
  GlobalMaterialLocalizations.delegate,
  GlobalWidgetsLocalizations.delegate,
  GlobalCupertinoLocalizations.delegate,
];

void _phone(WidgetTester t, {double scale = 1.0}) {
  // 720×1280, 320 dpi = 360×640 dp
  t.view.physicalSize = const Size(720, 1280);
  t.view.devicePixelRatio = 2.0;
  t.platformDispatcher.textScaleFactorTestValue = scale;
  // кыргызского нет в Material/Cupertino — Flutter только предупреждает (то же и в самом приложении); это не ошибка раскладки
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

GoRouter _router({String initial = '/sale'}) => GoRouter(
  initialLocation: initial,
  routes: [
    ShellRoute(
      builder: (c, s, child) => CashierShell(location: s.matchedLocation, child: child),
      routes: [
        for (final p in ['/home', '/sale', '/checks', '/stock']) GoRoute(path: p, builder: (_, _) => Center(child: Text('page $p'))),
      ],
    ),
  ],
);

Widget _app(GoRouter r, {String lang = 'ru', bool dark = false}) => MaterialApp.router(
  routerConfig: r,
  locale: Locale(lang),
  supportedLocales: AppLocalizations.supportedLocales,
  localizationsDelegates: _delegates,
  theme: buildTheme(dark: false),
  darkTheme: buildTheme(dark: true),
  themeMode: dark ? ThemeMode.dark : ThemeMode.light,
);

double _lum(Color c) {
  double f(double v) => v <= 0.03928 ? v / 12.92 : math.pow((v + 0.055) / 1.055, 2.4).toDouble();
  return 0.2126 * f(c.r) + 0.7152 * f(c.g) + 0.0722 * f(c.b);
}

double _contrast(Color a, Color b) {
  final x = _lum(a), y = _lum(b);
  return (math.max(x, y) + 0.05) / (math.min(x, y) + 0.05);
}

Widget _titleApp(Widget title) => MaterialApp(
  locale: const Locale('ru'),
  supportedLocales: AppLocalizations.supportedLocales,
  localizationsDelegates: _delegates,
  home: Scaffold(appBar: title as PreferredSizeWidget, body: const SizedBox()),
);

void main() {
  for (final lang in ['ru', 'ky']) {
    for (final scale in [1.0, 1.3]) {
      for (final dark in [false, true]) {
        testWidgets('оболочка без переполнения: $lang, шрифт $scale, тёмная=$dark', (t) async {
          _phone(t, scale: scale);
          await t.pumpWidget(_app(_router(initial: '/home'), lang: lang, dark: dark));
          await t.pumpAndSettle();
          expect(t.takeException(), isNull);
          // все пять вкладок кликабельны не меньше 48×48
          final inks = find.descendant(of: find.byType(CashierShell), matching: find.byType(InkWell));
          expect(inks, findsNWidgets(5));
          for (final e in inks.evaluate()) {
            final size = (e.renderObject! as RenderBox).size;
            expect(size.width, greaterThanOrEqualTo(48));
            expect(size.height, greaterThanOrEqualTo(48));
          }
        });
      }
    }
  }

  for (final scale in [1.0, 1.3, 2.0]) {
    testWidgets('шапка ScreenTitle без переполнения при шрифте $scale', (t) async {
      _phone(t, scale: scale);
      await t.pumpWidget(
        _titleApp(
          ScreenTitle(
            'Очень длинное название экрана для проверки',
            subtitle: 'Подпись под названием',
            actions: [IconButton(onPressed: () {}, icon: const Icon(Icons.search))],
          ),
        ),
      );
      await t.pumpAndSettle();
      expect(t.takeException(), isNull);
      // кнопка «Назад» — не меньше 48 dp
      final back = t.getSize(find.descendant(of: find.byType(BackSquare), matching: find.byType(SizedBox)).first);
      expect(back.width, greaterThanOrEqualTo(48));
      expect(back.height, greaterThanOrEqualTo(48));
      // шапка без подписи тоже
      await t.pumpWidget(_titleApp(const ScreenTitle('Оплата')));
      await t.pumpAndSettle();
      expect(t.takeException(), isNull);
    });
  }

  void mockExit(WidgetTester t, void Function() onExit) {
    t.binding.defaultBinaryMessenger.setMockMethodCallHandler(SystemChannels.platform, (call) async {
      if (call.method == 'SystemNavigator.pop') onExit();
      return null;
    });
    addTearDown(() => t.binding.defaultBinaryMessenger.setMockMethodCallHandler(SystemChannels.platform, null));
  }

  testWidgets('«Назад»: вкладка → Главная; на Главной — «нажмите ещё раз», второй раз выходит', (t) async {
    _phone(t);
    var exits = 0;
    mockExit(t, () => exits++);
    await t.pumpWidget(_app(_router(initial: '/stock')));
    await t.pumpAndSettle();
    expect(find.text('page /stock'), findsOneWidget);

    await t.binding.handlePopRoute(); // вкладка «Склад» → «Главная»
    await t.pumpAndSettle();
    expect(find.text('page /home'), findsOneWidget);
    expect(exits, 0);

    await t.binding.handlePopRoute(); // на «Главной» — подсказка, не выход
    await t.pumpAndSettle();
    expect(find.text('Нажмите ещё раз, чтобы выйти'), findsOneWidget);
    expect(exits, 0);

    await t.binding.handlePopRoute(); // второй раз подряд — выход
    await t.pumpAndSettle();
    expect(exits, 1);
  });

  testWidgets('«Назад» на «Чеках» тоже ведёт на «Главную», а не закрывает приложение', (t) async {
    _phone(t);
    var exits = 0;
    mockExit(t, () => exits++);
    await t.pumpWidget(_app(_router(initial: '/checks')));
    await t.pumpAndSettle();
    await t.binding.handlePopRoute();
    await t.pumpAndSettle();
    expect(find.text('page /home'), findsOneWidget);
    expect(exits, 0);
  });

  test('контраст AA (≥ 4,5:1) для «успех/ошибка/внимание» и основного текста в обеих темах', () {
    for (final dark in [false, true]) {
      final p = Palette(dark);
      for (final bg in [p.window, p.panel]) {
        for (final (name, fg) in [('success', p.success), ('danger', p.danger), ('warn', p.warn), ('text', p.text), ('textSoft', p.textSoft)]) {
          expect(_contrast(fg, bg), greaterThanOrEqualTo(4.5), reason: '$name на фоне, тёмная=$dark');
        }
      }
      // инициалы на аватаре
      expect(_contrast(p.onAvatar, p.avatarBg), greaterThanOrEqualTo(4.5));
    }
  });

  test('в тёмной теме аватар и «героевая» карточка отличаются от фона окна', () {
    const p = Palette(true);
    expect(p.avatarBg, isNot(p.window));
    expect(p.heroBg, isNot(p.window));
    expect(_contrast(p.heroBg, p.window), greaterThan(1.1));
    expect(_contrast(p.panel, p.window), greaterThan(1.05));
    expect(_contrast(p.border, p.window), greaterThan(1.5));
  });
}
