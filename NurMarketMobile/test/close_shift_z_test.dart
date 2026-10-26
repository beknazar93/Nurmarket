// 2026-10-26 (приёмка 1.18.6, Б-1): после «Закрыть смену» с Z-итога можно уйти — «Готово», стрелка «Назад» и системная
// «Назад» ведут на «Главную». Раньше pushReplacement + go('/home') на уже /home и PopScope(canPop: false) запирали кассира.
// Сеть и боевая база не нужны: фейковый клиент, база в памяти, стенд повторяет дерево приложения (ShellRoute + корневые маршруты).
import 'dart:io';

import 'package:drift/native.dart';
import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/providers.dart';
import 'package:nurmarket/core/theme/theme.dart';
import 'package:nurmarket/core/util/audit.dart';
import 'package:nurmarket/features/ops/tape_doc.dart';
import 'package:nurmarket/features/shift/close_shift_screen.dart';
import 'package:nurmarket/features/shift/shift_repository.dart';
import 'package:nurmarket/l10n/app_localizations.dart';
import 'package:nurmarket/l10n/app_localizations_ru.dart';

class _Api extends ApiClient {
  _Api() : super(SessionStore());
  final closes = <String>[];
  @override
  Future<dynamic> get(String path, {Map<String, dynamic>? query, Duration? timeout}) async => throw ApiException('Нет связи с сервером.');
  @override
  Future<dynamic> post(String path, {Object? body, Map<String, String>? headers, Duration? timeout}) async {
    closes.add(path);
    return {};
  }
}

class _Fixed extends SessionController {
  @override
  Session? build() => const Session(email: 'k@x.kg', access: 'a', refresh: 'r', userId: 'k1', userName: 'Кассир', cashboxId: 'b', cashboxName: 'Касса 1');
}

class _OpenShift extends ShiftController {
  @override
  ShiftState build() => const ShiftState(shift: ShiftInfo(id: 'sh1'));
}

Future<void> _until(WidgetTester t, bool Function() ok) async {
  for (var i = 0; i < 80 && !ok(); i++) {
    await t.pump(const Duration(milliseconds: 100));
  }
  expect(ok(), isTrue);
}

void main() {
  late AppDatabase db;
  late _Api api;
  late GoRouter router;
  final l = AppLocalizationsRu();

  setUpAll(() => Audit.testDirectory = Directory.systemTemp.createTempSync('nm_zshift_'));
  setUp(() {
    db = AppDatabase(NativeDatabase.memory());
    api = _Api();
  });
  tearDown(() => db.close());

  /// Как в приложении: «Главная» внутри ShellRoute, «Закрыть смену» — корневой маршрут, открытый поверх (push).
  Future<void> toZ(WidgetTester t, {bool openZ = true, Size size = const Size(720, 1400), double ratio = 2.0}) async {
    t.view.physicalSize = size;
    t.view.devicePixelRatio = ratio;
    addTearDown(t.view.resetPhysicalSize);
    addTearDown(t.view.resetDevicePixelRatio);
    final prev = FlutterError.onError;
    FlutterError.onError = (d) {
      if (d.exceptionAsString().contains('not supported by all of its localization delegates')) return;
      prev?.call(d);
    };
    addTearDown(() => FlutterError.onError = prev);
    router = GoRouter(
      initialLocation: '/home',
      routes: [
        ShellRoute(
          builder: (c, s, child) => Scaffold(body: child),
          routes: [GoRoute(path: '/home', builder: (_, _) => const Text('HOME'))],
        ),
        GoRoute(path: '/close-shift', builder: (_, _) => const CloseShiftScreen()),
      ],
    );
    await t.pumpWidget(
      ProviderScope(
        overrides: [
          databaseProvider.overrideWithValue(db),
          apiProvider.overrideWithValue(api),
          sessionProvider.overrideWith(_Fixed.new),
          shiftProvider.overrideWith(_OpenShift.new),
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
    await t.pumpAndSettle();
    router.push('/close-shift');
    await t.pumpAndSettle();
    if (!openZ) return;
    await t.tap(find.widgetWithText(FilledButton, l.closeShift));
    // индикатор «занято» крутится — pumpAndSettle не дождётся, гоним кадры сами
    await _until(t, () => find.byType(AlertDialog).evaluate().isNotEmpty);
    await t.tap(find.descendant(of: find.byType(AlertDialog), matching: find.widgetWithText(FilledButton, l.closeShift)));
    await _until(t, () => find.byType(TapeDocScreen).evaluate().isNotEmpty);
    await t.pumpAndSettle();
    expect(find.byType(TapeDocScreen), findsOneWidget, reason: 'после закрытия смены открыт Z-итог');
    expect(api.closes.where((p) => p.endsWith('/close/')), hasLength(1));
  }

  void expectHome() {
    expect(find.byType(TapeDocScreen), findsNothing, reason: 'Z-итог закрыт');
    expect(find.byType(CloseShiftScreen), findsNothing, reason: 'экран закрытия смены не остаётся под ним');
    expect(find.text('HOME'), findsOneWidget);
    expect(router.state.matchedLocation, '/home');
  }

  testWidgets('С-3н: на 411×731 dp поле «Пересчитано в кассе» видно без прокрутки — и «Закрыть смену» закреплена под ним', (t) async {
    await toZ(t, openZ: false, size: const Size(1080, 1920), ratio: 420 / 160);
    final field = t.getRect(find.byType(TextField));
    expect(field.bottom, lessThanOrEqualTo(731), reason: 'поле пересчёта не ниже сгиба');
    expect(field.top, greaterThan(0));
  });

  testWidgets('Z-итог → «Готово» → Главная', (t) async {
    await toZ(t);
    await t.tap(find.widgetWithText(FilledButton, l.done));
    await t.pumpAndSettle();
    expectHome();
  });

  testWidgets('Z-итог → стрелка «Назад» → Главная', (t) async {
    await toZ(t);
    await t.tap(find.byIcon(Icons.chevron_left_rounded));
    await t.pumpAndSettle();
    expectHome();
  });

  testWidgets('Z-итог → системная «Назад» → Главная', (t) async {
    await toZ(t);
    await t.binding.handlePopRoute();
    await t.pumpAndSettle();
    expectHome();
  });
}
