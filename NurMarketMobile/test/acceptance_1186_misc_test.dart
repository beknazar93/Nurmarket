// 2026-10-26 (приёмка 1.18.6): технический текст ⟦nm⟧ не виден в «Задачах» (С-7н) и в «Зарплате», подсказки Советника — касание 48 dp
// (Н-6н). Всё на фейках: сервер в памяти, ключа ИИ нет, сети нет.
import 'dart:io';

import 'package:drift/drift.dart' show driftRuntimeOptions;
import 'package:drift/native.dart';
import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_riverpod/misc.dart' show Override;
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/ai/assistant/staff_tools.dart' show decodeTaskMeta, encodeTaskMeta;
import 'package:nurmarket/core/ai/vision.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/providers.dart';
import 'package:nurmarket/core/theme/theme.dart';
import 'package:nurmarket/core/util/audit.dart';
import 'package:nurmarket/features/admin/advisor.dart';
import 'package:nurmarket/features/staff/tasks_screen.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

import 'support/staff_fakes.dart';

class _Fixed extends SessionController {
  final Session s;
  _Fixed(this.s);
  @override
  Session? build() => s;
}

class _NoKeyVision extends VisionService {
  _NoKeyVision(super.api, super.db);
  @override
  Future<String?> key() async => null;
  @override
  Future<({String? groq, String? openRouter})> fallbackKeys() async => (groq: null, openRouter: null);
}

const _owner = Session(email: 'o@x.kg', access: 'a', refresh: 'r', userId: 'owner1', userName: 'Бакыт О.', role: 'owner', isOwner: true, companyName: 'Нур', cashboxId: 'b', cashboxName: 'Касса 1');
const _delegates = <LocalizationsDelegate<dynamic>>[AppLocalizations.delegate, GlobalMaterialLocalizations.delegate, GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate];

void _phone(WidgetTester t, {double scale = 1.0}) {
  t.view.physicalSize = const Size(720, 1280);
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

void main() {
  setUpAll(() => Audit.testDirectory = Directory.systemTemp.createTempSync('nm_misc1186_'));

  group('С-7н: технический текст', () {
    test('подсказки «Задач» и «Зарплаты» на всех языках без ⟦nm⟧ и «[#сотрудник период]»', () {
      for (final code in ['ru', 'ky', 'en', 'tr', 'uz']) {
        final l = lookupAppLocalizations(Locale(code));
        for (final text in [l.stfPriorityHint, l.stfSalaryHint]) {
          expect(text, isNot(contains('⟦')), reason: code);
          expect(text, isNot(contains('⟧')), reason: code);
          expect(text, isNot(contains('[#')), reason: code);
        }
      }
    });

    test('служебная строка в описании задачи не попадает в текст описания (сайт мог записать её в конец)', () {
      final raw = encodeTaskMeta('Пересчитать полку', priority: 'high', remindAt: DateTime(2026, 10, 27, 9));
      expect(raw, contains('⟦nm:'));
      final d = decodeTaskMeta(raw);
      expect(d.text, 'Пересчитать полку');
      expect(d.priority, 'high');
      expect(decodeTaskMeta('Только текст ⟦nm: priority=low⟧').text, 'Только текст');
    });

    for (final lang in ['ru', 'ky']) {
      testWidgets('$lang: экран «Задачи» с задачей, у которой в описании служебная строка, не показывает ⟦nm⟧ нигде', (t) async {
        _phone(t, scale: 1.3);
        final db = AppDatabase(NativeDatabase.memory());
        addTearDown(db.close);
        final srv = StaffServer();
        srv.addTask('Пересчитать полку с водой', 'u2', due: '2026-10-27', description: 'Сначала верхние ⟦nm: priority=high; remind=2026-10-27T09:00⟧');
        await t.pumpWidget(
          ProviderScope(
            overrides: [databaseProvider.overrideWithValue(db), pendingCountProvider.overrideWith((ref) => Stream.value(0)), apiProvider.overrideWithValue(FakeApi(srv.handle)), sessionProvider.overrideWith(() => _Fixed(_owner))],
            child: MaterialApp(theme: buildTheme(dark: false), locale: Locale(lang), supportedLocales: AppLocalizations.supportedLocales, localizationsDelegates: _delegates, home: const TasksScreen()),
          ),
        );
        await t.pumpAndSettle();
        expect(t.takeException(), isNull);
        expect(find.textContaining('Пересчитать полку с водой'), findsOneWidget);
        expect(find.textContaining('⟦'), findsNothing);
        expect(find.textContaining('nm:'), findsNothing);
        // и в окне новой задачи
        final l = lookupAppLocalizations(Locale(lang));
        await t.tap(find.byTooltip(l.stfNewTask));
        await t.pumpAndSettle();
        expect(find.textContaining('⟦'), findsNothing);
      });
    }
  });

  group('Н-6н: подсказки Советника', () {
    late AppDatabase db;
    setUp(() {
      driftRuntimeOptions.dontWarnAboutMultipleDatabases = true;
      db = AppDatabase(NativeDatabase.memory());
    });
    tearDown(() => db.close());

    for (final (lang, scale) in [('ru', 1.0), ('ky', 1.3)]) {
      testWidgets('$lang, шрифт $scale: ряд подсказок над полем ввода — область касания чипа ≥ 48 dp', (t) async {
        _phone(t, scale: scale);
        final List<Override> noKey = [visionProvider.overrideWithValue(_NoKeyVision(ApiClient(SessionStore()), db))];
        await t.pumpWidget(
          ProviderScope(
            overrides: noKey,
            child: MaterialApp(theme: buildTheme(dark: false), locale: Locale(lang), supportedLocales: AppLocalizations.supportedLocales, localizationsDelegates: _delegates, home: const AdvisorScreen()),
          ),
        );
        await t.pump();
        final l = lookupAppLocalizations(Locale(lang));
        // пустой чат — подсказки в приветствии; нажатие переводит разговор в режим, где ряд подсказок стоит над полем ввода
        expect(find.text(l.advisorGreeting), findsOneWidget);
        // «Нажатие» на подсказку напрямую (как её нажал бы палец): chip.onPressed
        t.widget<ActionChip>(find.ancestor(of: find.text(l.advisorQ1), matching: find.byType(ActionChip)).first).onPressed!();
        await t.pump();
        await t.pump(const Duration(milliseconds: 300));
        expect(find.text(l.advisorGreeting), findsNothing, reason: 'разговор начался — приветствие ушло, подсказки остались только в ряду над полем');
        final chips = find.byType(ActionChip);
        expect(chips, findsWidgets, reason: 'ряд подсказок над полем ввода');
        for (var i = 0; i < chips.evaluate().length; i++) {
          expect(t.getSize(chips.at(i)).height, greaterThanOrEqualTo(48), reason: 'подсказка $i');
        }
        await t.pumpAndSettle(const Duration(seconds: 1));
      });
    }
  });
}
