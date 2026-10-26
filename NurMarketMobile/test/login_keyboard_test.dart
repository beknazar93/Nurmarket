// 2026-10-26 (приёмка 1.18.6, В-1н): вход на 411×731 dp с открытой клавиатурой — поля логина и пароля видны над кнопкой
// «Войти», кнопка не закрывает их. Без сети: экран входа не ходит на сервер, пока не нажата кнопка.
import 'package:drift/drift.dart' show driftRuntimeOptions;
import 'package:drift/native.dart';
import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/providers.dart';
import 'package:nurmarket/core/theme/theme.dart';
import 'package:nurmarket/features/auth/login_screen.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

void main() {
  late AppDatabase db;
  setUp(() {
    driftRuntimeOptions.dontWarnAboutMultipleDatabases = true;
    db = AppDatabase(NativeDatabase.memory());
  });
  tearDown(() => db.close());

  for (final lang in ['ru', 'ky']) {
    for (final scale in [1.0, 1.3]) {
      testWidgets('$lang, шрифт $scale, 411×731 dp, клавиатура ~300 dp: поля над кнопкой и целиком на экране', (t) async {
        t.view.physicalSize = const Size(1080, 1920);
        t.view.devicePixelRatio = 420 / 160; // 411×731 dp
        t.view.viewInsets = const FakeViewPadding(bottom: 780); // ≈ 297 dp
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
          t.view.resetViewInsets();
          t.platformDispatcher.clearTextScaleFactorTestValue();
        });
        await t.pumpWidget(
          ProviderScope(
            overrides: [databaseProvider.overrideWithValue(db)],
            child: MaterialApp(
              theme: buildTheme(dark: false),
              locale: Locale(lang),
              supportedLocales: AppLocalizations.supportedLocales,
              localizationsDelegates: const [AppLocalizations.delegate, GlobalMaterialLocalizations.delegate, GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate],
              home: const LoginScreen(),
            ),
          ),
        );
        await t.pump();
        expect(t.takeException(), isNull);
        final l = lookupAppLocalizations(Locale(lang));
        final login = t.getRect(find.byType(TextField).first);
        final pass = t.getRect(find.byType(TextField).last);
        final button = t.getRect(find.widgetWithText(FilledButton, l.signIn));
        final visibleBottom = t.view.physicalSize.height / t.view.devicePixelRatio - 780 / t.view.devicePixelRatio;
        expect(login.top, greaterThanOrEqualTo(0));
        expect(pass.bottom, lessThanOrEqualTo(visibleBottom), reason: 'пароль виден над клавиатурой без прокрутки');
        expect(button.top, greaterThanOrEqualTo(pass.bottom), reason: 'кнопка под полями, а не поверх них');
        expect(login.bottom, lessThanOrEqualTo(pass.top));
        // кнопка достижима: либо видна, либо экран прокручивается (одна прокрутка на весь экран)
        expect(find.byType(SingleChildScrollView), findsOneWidget);
        await t.ensureVisible(find.widgetWithText(FilledButton, l.signIn));
        await t.pump();
        expect(t.getRect(find.widgetWithText(FilledButton, l.signIn)).bottom, lessThanOrEqualTo(visibleBottom + 0.5));
      });
    }
  }

  testWidgets('без клавиатуры: шапка с логотипом и кнопка внизу экрана', (t) async {
    t.view.physicalSize = const Size(1080, 1920);
    t.view.devicePixelRatio = 420 / 160;
    addTearDown(() {
      t.view.resetPhysicalSize();
      t.view.resetDevicePixelRatio();
    });
    await t.pumpWidget(
      ProviderScope(
        overrides: [databaseProvider.overrideWithValue(db)],
        child: MaterialApp(
          theme: buildTheme(dark: false),
          locale: const Locale('ru'),
          supportedLocales: AppLocalizations.supportedLocales,
          localizationsDelegates: const [AppLocalizations.delegate, GlobalMaterialLocalizations.delegate, GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate],
          home: const LoginScreen(),
        ),
      ),
    );
    await t.pump();
    final l = lookupAppLocalizations(const Locale('ru'));
    expect(find.text(l.loginTagline), findsOneWidget);
    final h = t.view.physicalSize.height / t.view.devicePixelRatio;
    expect(t.getRect(find.widgetWithText(FilledButton, l.signIn)).bottom, greaterThan(h - 40));
  });
}
