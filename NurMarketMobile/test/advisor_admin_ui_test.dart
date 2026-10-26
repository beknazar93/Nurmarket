// 2026-10-26 (живой аудит, П10 «Управление» и П11 «Советник: чат»): плитки-показатели одной высоты и «нет связи / Повторить»,
// разбор Markdown (таблицы, жирный, списки), приветствие, заметный звонок и проверка ключа до звонка — на фейках, без сети и микрофона.
import 'package:drift/drift.dart' show driftRuntimeOptions;
import 'package:drift/native.dart';
import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_riverpod/misc.dart' show Override;
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/ai/vision.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/theme/theme.dart';
import 'package:nurmarket/core/util/ui.dart';
import 'package:nurmarket/features/admin/admin_screens.dart';
import 'package:nurmarket/features/admin/advisor.dart';
import 'package:nurmarket/features/admin/advisor_call.dart';
import 'package:nurmarket/features/admin/advisor_markdown.dart';
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
  // кыргызского нет в Material/Cupertino — Flutter только предупреждает; это не ошибка раскладки
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

Widget _app(Widget home, {String lang = 'ru', bool dark = false, List<Override> overrides = const []}) => ProviderScope(
  overrides: overrides,
  child: MaterialApp(
    locale: Locale(lang),
    supportedLocales: AppLocalizations.supportedLocales,
    localizationsDelegates: _delegates,
    theme: buildTheme(dark: false),
    darkTheme: buildTheme(dark: true),
    themeMode: dark ? ThemeMode.dark : ThemeMode.light,
    home: home,
  ),
);

class _NoKeyVision extends VisionService {
  _NoKeyVision(super.api, super.db);
  @override
  Future<String?> key() async => null;
  @override
  Future<({String? groq, String? openRouter})> fallbackKeys() async => (groq: null, openRouter: null);
}

void main() {
  group('Markdown ответа Советника', () {
    test('таблица: шапка, строки, выравнивание, «\\|» внутри ячейки, короткая строка добивается', () {
      const src = 'Продажи за неделю:\n\n'
          '| Товар | Штук | Выручка |\n'
          '|:---|---:|---:|\n'
          '| Хлеб | 12 | 1 200 |\n'
          '| Молоко 1\\|2 | 5 | 650 |\n'
          '| Итого | 17 |\n\n'
          'Хорошая неделя.';
      final b = parseMarkdown(src);
      expect(b.map((e) => e.kind), [MdKind.para, MdKind.table, MdKind.para]);
      final t = b[1];
      expect(t.header, ['Товар', 'Штук', 'Выручка']);
      expect(t.aligns, [-1, 1, 1]);
      expect(t.rows.length, 3);
      expect(t.rows[1][0], 'Молоко 1|2');
      expect(t.rows[2], ['Итого', '17', '']);
    });

    test('таблица без крайних «|» и строка-разделитель без пробелов', () {
      final b = parseMarkdown('Имя | Долг\n--|--\nАйбек | 500\nНурия | 120');
      expect(b.single.kind, MdKind.table);
      expect(b.single.rows, [
        ['Айбек', '500'],
        ['Нурия', '120'],
      ]);
    });

    test('заголовки, списки, нумерация, линия, цитата, код', () {
      final b = parseMarkdown('# Итоги\n- один\n* два\n  - вложенный\n1. первый\n2) второй\n---\n> важно\n```\nа | б\n```');
      expect(b.map((e) => e.kind), [
        MdKind.heading,
        MdKind.bullet,
        MdKind.bullet,
        MdKind.bullet,
        MdKind.numbered,
        MdKind.numbered,
        MdKind.rule,
        MdKind.quote,
        MdKind.code,
      ]);
      expect(b[0].level, 1);
      expect(b[3].level, 1);
      expect(b[5].marker, '2)');
      expect(b.last.text, 'а | б'); // внутри ``` таблицы нет
    });

    test('жирный, курсив, код; snake_case и «2 * 3» не ломаются, незакрытое остаётся текстом', () {
      expect(mdPlain('Выручка **1 200 сом** и *рост*, `код`'), 'Выручка 1 200 сом и рост, код');
      final sp = parseInline('a **b** c');
      expect(sp.map((e) => (e.text, e.bold)), [('a ', false), ('b', true), (' c', false)]);
      expect(mdPlain('my_var_name и 2 * 3 * 4'), 'my_var_name и 2 * 3 * 4');
      expect(mdPlain('**без конца'), '**без конца');
      expect(mdPlain('_курсив_ и snake_case_x'), 'курсив и snake_case_x');
    });

    testWidgets('ответ с таблицей рисуется таблицей, без сырых «|» и «**», широкая — прокручивается вбок', (t) async {
      _phone(t);
      const src = '## Должники\n\n**Всего 3 клиента**\n\n'
          '| Клиент | Долг | Телефон | Последний чек | Комментарий к долгу очень длинный |\n'
          '|---|---:|---|---|---|\n'
          '| Айбек | 500 | 0700 111 222 | 12.10 | вернёт в пятницу |\n'
          '| Нурия | 1 200 | 0700 333 444 | 10.10 | просила подождать |\n'
          '- напомнить **сегодня**';
      await t.pumpWidget(_app(const Scaffold(body: SingleChildScrollView(child: Padding(padding: EdgeInsets.all(16), child: MdView(src, color: Colors.black))))));
      expect(t.takeException(), isNull);
      expect(find.byType(Table), findsOneWidget);
      expect(find.text('Клиент'), findsOneWidget);
      expect(find.textContaining('|'), findsNothing);
      expect(find.textContaining('**'), findsNothing);
      expect(find.byWidgetPredicate((w) => w is SingleChildScrollView && w.scrollDirection == Axis.horizontal), findsOneWidget);
      // таблица шире экрана 360 dp, но не ломает раскладку — прокручивается
      final sc = t.state<ScrollableState>(find.descendant(of: find.byType(MdTable), matching: find.byType(Scrollable)));
      expect(sc.position.maxScrollExtent, greaterThan(0));
    });
  });

  group('«Управление»: плитки', () {
    Widget row({required bool failed, VoidCallback? retry}) => Scaffold(
      body: Padding(
        padding: const EdgeInsets.all(16),
        child: Builder(
          builder: (c) => Column(
            children: [
              IntrinsicHeight(
                child: Row(
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: [
                    Expanded(child: AdminKpiTile(key: const Key('a'), label: c.l.revenueToday, value: failed ? null : '1 065 ${c.l.som}', failed: failed, onRetry: retry)),
                    const SizedBox(width: 10),
                    Expanded(child: AdminKpiTile(key: const Key('b'), label: c.l.checksToday, value: failed ? null : '3', failed: failed, onRetry: retry)),
                  ],
                ),
              ),
              const SizedBox(height: 10),
              IntrinsicHeight(
                child: Row(
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: [
                    Expanded(child: AdminKpiTile(key: const Key('c'), label: c.l.runningOutShort, value: '39')),
                    const SizedBox(width: 10),
                    Expanded(child: AdminKpiTile(key: const Key('d'), label: c.l.clientsOwe, value: failed ? null : '394 ${c.l.som}', failed: failed, onRetry: retry)),
                  ],
                ),
              ),
            ],
          ),
        ),
      ),
    );

    for (final lang in ['ru', 'ky']) {
      for (final scale in [1.0, 1.3]) {
        for (final failed in [false, true]) {
          testWidgets('$lang, шрифт $scale, ${failed ? 'нет связи' : 'есть связь'}: без переполнения, плитки ряда одной высоты', (t) async {
            _phone(t, scale: scale);
            await t.pumpWidget(_app(row(failed: failed, retry: () {}), lang: lang));
            expect(t.takeException(), isNull);
            expect(t.getSize(find.byKey(const Key('a'))).height, t.getSize(find.byKey(const Key('b'))).height);
            expect(t.getSize(find.byKey(const Key('c'))).height, t.getSize(find.byKey(const Key('d'))).height);
            if (!failed) {
              // 2026-10-26: «Заканчиваются» + число, а не «Заканчиваются 39 товаров / 39»
              expect(find.text(lookupAppLocalizations(Locale(lang)).runningOutShort), findsOneWidget);
              expect(find.textContaining('39 '), findsNothing);
            }
          });
        }
      }
    }

    testWidgets('без связи: «нет связи» и «Повторить», касание вызывает повтор; грузится — «…»', (t) async {
      _phone(t);
      var n = 0;
      await t.pumpWidget(_app(row(failed: true, retry: () => n++)));
      expect(find.text('нет связи'), findsNWidgets(3));
      expect(find.text('Повторить'), findsNWidgets(3));
      await t.tap(find.text('Повторить').first);
      expect(n, 1);
      await t.pumpWidget(_app(const Scaffold(body: AdminKpiTile(label: 'Выручка', value: null))));
      expect(find.text('…'), findsOneWidget);
      expect(find.text('нет связи'), findsNothing);
    });
  });

  group('Советник: чат', () {
    late AppDatabase db;
    setUp(() {
      driftRuntimeOptions.dontWarnAboutMultipleDatabases = true;
      db = AppDatabase(NativeDatabase.memory());
    });

    List<Override> noKey() => [visionProvider.overrideWithValue(_NoKeyVision(ApiClient(SessionStore()), db))];

    for (final lang in ['ru', 'ky']) {
      testWidgets('$lang: пустой чат — приветствие и подсказки; звонок виден с подписью (шапка и кнопка под полем)', (t) async {
        _phone(t, scale: 1.3);
        final l = lookupAppLocalizations(Locale(lang));
        await t.pumpWidget(_app(const AdvisorScreen(), lang: lang, overrides: noKey()));
        await t.pump();
        expect(t.takeException(), isNull);
        expect(find.text(l.advisorGreeting), findsOneWidget);
        for (final q in [l.advisorQ1, l.advisorQ2, l.advisorQ3, l.advisorQ4]) {
          expect(find.text(q), findsOneWidget);
        }
        // кнопка с подписью + значок в шапке с подсказкой
        expect(find.widgetWithText(FilledButton, l.a11yCallAi), findsOneWidget);
        expect(find.byTooltip(l.a11yCallAi), findsOneWidget);
        expect(t.getSize(find.widgetWithText(FilledButton, l.a11yCallAi)).height, greaterThanOrEqualTo(48));
      });
    }

    testWidgets('звонок без ключа: сообщение «нет ключа», экран звонка (и микрофон) не открывается', (t) async {
      _phone(t);
      final l = lookupAppLocalizations(const Locale('ru'));
      await t.pumpWidget(_app(const AdvisorScreen(), overrides: noKey()));
      await t.tap(find.widgetWithText(FilledButton, l.a11yCallAi));
      await t.pump();
      await t.pump(const Duration(milliseconds: 300));
      expect(find.byType(AdvisorCallScreen), findsNothing);
      expect(find.text(l.advisorNoKey), findsOneWidget);
    });

    testWidgets('тёмная тема: приветствие и кнопка звонка без исключений', (t) async {
      _phone(t);
      await t.pumpWidget(_app(const AdvisorScreen(), dark: true, overrides: noKey()));
      await t.pump();
      expect(t.takeException(), isNull);
    });
  });
}
