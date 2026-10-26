// 2026-10-26 (голос-управление, В5): экран «Помощник» (/ai/settings) на фейках — без сети, микрофона и боевой базы.
// Проверяем: три режима радиокарточками (≥ 72 dp, «по умолчанию»), владелец/администратор меняют режим для всех, сотрудник —
// только для себя и только строже («владелец задал», мягче — затенено и не нажимается), действует более строгий из двух (так
// же читает движок звонка), «Голосом режим не меняется» (в коде звонка нет ни инструмента, ни записи настройки), строка статуса
// доступа к ИИ, маршруты и реестр экранов, раскладка ru/ky, шрифт 1.0 и 1.3, светлая и тёмная тема.
import 'dart:io';

import 'package:drift/native.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';
import 'package:nurmarket/core/ai/ai_access.dart';
import 'package:nurmarket/core/ai/assistant/action_tools.dart';
import 'package:nurmarket/core/ai/assistant/ai_journal.dart';
import 'package:nurmarket/core/ai/assistant/screen_registry.dart';
import 'package:nurmarket/core/ai/assistant/tool_registry.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/providers.dart';
import 'package:nurmarket/core/theme/theme.dart';
import 'package:nurmarket/core/util/audit.dart';
import 'package:nurmarket/features/assistant/assistant_mode.dart';
import 'package:nurmarket/features/assistant/assistant_settings.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

import 'action_tools_test.dart' show FakeHost;

class _Fixed extends SessionController {
  final Session s;
  _Fixed(this.s);
  @override
  Session? build() => s;
}

const _owner = Session(email: 'o@x.kg', access: 'a', refresh: 'r', userId: 'u2', userName: 'Бакыт О.', role: 'owner', isOwner: true, companyName: 'Нур', cashboxId: 'b');
const _cashier = Session(email: 'k@x.kg', access: 'a', refresh: 'r', userId: 'u1', userName: 'Айгерим С.', role: 'cashier', companyName: 'Нур', cashboxId: 'b', perms: []);

void _phone(WidgetTester t, {double scale = 1.0}) {
  t.view.physicalSize = const Size(720, 1480); // 360×740 dp
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

Future<void> _fonts() async {
  final m = FontLoader('Montserrat');
  for (final w in ['Regular', 'Medium', 'SemiBold', 'Bold', 'ExtraBold']) {
    m.addFont(rootBundle.load('assets/fonts/Montserrat-$w.ttf'));
  }
  await m.load();
}

void main() {
  late AppDatabase db;
  late Directory dir;
  setUpAll(() async {
    await _fonts();
    dir = Directory.systemTemp.createTempSync('nm_settings_');
    Audit.testDirectory = dir;
    AiJournal.testDirectory = dir;
  });
  tearDownAll(() {
    try {
      dir.deleteSync(recursive: true);
    } catch (_) {}
  });
  setUp(() {
    AiJournal.resetQueueForTests();
    Audit.resetForTests();
    db = AppDatabase(NativeDatabase.memory());
  });
  tearDown(() => db.close());

  late List<String> pushed;
  Widget app(Session s, {String lang = 'ru', bool dark = false, AiAccessPolicy policy = const AiAccessPolicy.unset()}) {
    pushed = [];
    final router = GoRouter(
      routes: [
        GoRoute(path: '/', builder: (_, _) => const AssistantSettingsScreen()),
        GoRoute(
          path: '/ai/log',
          builder: (_, _) {
            pushed.add('/ai/log');
            return const Scaffold(body: Text('LOG'));
          },
        ),
      ],
    );
    return ProviderScope(
      key: UniqueKey(), // новая область на каждый pumpWidget: иначе Riverpod оставит прежнее значение aiPolicyProvider
      overrides: [
        databaseProvider.overrideWithValue(db),
        sessionProvider.overrideWith(() => _Fixed(s)),
        aiPolicyProvider.overrideWith((ref) async => policy),
      ],
      child: MaterialApp.router(
        routerConfig: router,
        theme: buildTheme(dark: false),
        darkTheme: buildTheme(dark: true),
        themeMode: dark ? ThemeMode.dark : ThemeMode.light,
        locale: Locale(lang),
        supportedLocales: AppLocalizations.supportedLocales,
        localizationsDelegates: const [AppLocalizations.delegate, GlobalMaterialLocalizations.delegate, GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate],
      ),
    );
  }

  /// Реальные ввод-вывод и база идут вне «поддельных» часов теста: ждём (по кругу «реальная пауза → кадр»), пока экран загрузится
  /// (и пока не выполнится [until]); на нагруженном ПК фиксированной паузы не хватает.
  Future<void> settle(WidgetTester t, {bool Function()? until}) async {
    for (var i = 0; i < 200; i++) {
      await t.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 50)));
      await t.pump(const Duration(milliseconds: 50)); // со временем: Future.delayed(zero) внутри журнала тоже срабатывает
      final loading = find.byType(CircularProgressIndicator).evaluate().isNotEmpty;
      if (i >= 2 && !loading && (until == null || until())) break;
    }
    await t.pumpAndSettle(const Duration(milliseconds: 100), EnginePhase.sendSemanticsUpdate, const Duration(seconds: 5));
  }

  Future<String?> setting(WidgetTester t, String key) async => (await t.runAsync<String?>(() => db.getSetting(key)));

  Finder card(String title) => find.ancestor(of: find.text(title), matching: find.byType(InkWell)).first;

  /// Нажать карточку режима и дождаться, пока значение реально легло в базу (запись идёт вне «поддельных» часов).
  Future<void> pick(WidgetTester t, String title, String key, String? value) async {
    await t.tap(card(title));
    for (var i = 0; i < 200; i++) {
      await t.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 50)));
      await t.pump(const Duration(milliseconds: 50));
      if (await setting(t, key) == value) break;
    }
    await settle(t);
  }

  group('режимы: владелец и администратор', () {
    testWidgets('по умолчанию выбрано «Выполнять по просьбе» с отметкой «по умолчанию»; карточки не ниже 72 dp', (t) async {
      _phone(t);
      await t.pumpWidget(app(_owner));
      await settle(t);
      expect(find.text('Выполнять по просьбе'), findsOneWidget);
      expect(find.text('по умолчанию'), findsOneWidget);
      expect(find.text('Только смотреть'), findsOneWidget);
      expect(find.text('Спрашивать перед деньгами'), findsOneWidget);
      for (final title in ['Выполнять по просьбе', 'Спрашивать перед деньгами', 'Только смотреть']) {
        expect(t.getSize(card(title)).height, greaterThanOrEqualTo(72), reason: title);
      }
      // выбран один: у выбранного карточки толстая рамка — проверяем по семантике «выбрано»
      expect(find.text('Владелец задал для всех на этом телефоне:'), findsNothing);
      expect(find.text('Голосом режим не меняется — только здесь'), findsOneWidget);
      expect(find.text('Действия ИИ'), findsOneWidget);
    });

    testWidgets('«Спрашивать перед деньгами» → сохраняется для всех на телефоне (assistant.mode), появляется «Перед чем спросит»', (t) async {
      _phone(t);
      await t.pumpWidget(app(_owner));
      await settle(t);
      expect(find.text('Перед чем спросит'), findsNothing);
      await pick(t, 'Спрашивать перед деньгами', 'assistant.mode', 'ask_money');
      expect(await setting(t, 'assistant.mode'), 'ask_money');
      expect(find.text('Перед чем спросит'), findsOneWidget);
      expect(find.textContaining('оплата · возврат'), findsOneWidget);
      expect(find.text('Режим сохранён'), findsOneWidget);
      // движок звонка читает тот же режим
      expect(await t.runAsync(() => AssistantModePrefs.load(db, 'u2')), AssistantMode.askMoney);
      await pick(t, 'Только смотреть', 'assistant.mode', 'view_only');
      expect(await t.runAsync(() => AssistantModePrefs.load(db, 'u2')), AssistantMode.viewOnly);
      await pick(t, 'Выполнять по просьбе', 'assistant.mode', 'act');
      expect(await t.runAsync(() => AssistantModePrefs.load(db, 'u2')), AssistantMode.act);
    });

    testWidgets('владелец может вернуть мягче: прежний собственный режим не держит строгий', (t) async {
      _phone(t);
      await t.runAsync(() => AssistantModePrefs.save(db, AssistantMode.viewOnly, userId: 'u2'));
      await t.pumpWidget(app(_owner));
      await settle(t);
      await pick(t, 'Выполнять по просьбе', 'assistant.mode.u2', 'act');
      expect(await t.runAsync(() => AssistantModePrefs.load(db, 'u2')), AssistantMode.act);
    });
  });

  group('режимы: сотрудник — только строже общего', () {
    testWidgets('владелец задал «Спрашивать перед деньгами»: плашка и метка «владелец», «Выполнять по просьбе» затенено и не нажимается', (t) async {
      _phone(t);
      await t.runAsync(() => AssistantModePrefs.save(db, AssistantMode.askMoney));
      await t.pumpWidget(app(_cashier));
      await settle(t);
      expect(find.text('Владелец задал для всех на этом телефоне: '), findsNothing); // текст внутри Text.rich
      expect(find.textContaining('Владелец задал для всех на этом телефоне:', findRichText: true), findsOneWidget);
      expect(find.textContaining('Для себя можно выбрать строже', findRichText: true), findsOneWidget);
      expect(find.text('владелец'), findsOneWidget);
      expect(find.text('Мягче, чем задал владелец — недоступно'), findsOneWidget);
      await t.tap(card('Выполнять по просьбе'), warnIfMissed: false);
      await settle(t);
      expect(await setting(t, 'assistant.mode.u1'), isNull);
      expect(await setting(t, 'assistant.mode'), 'ask_money');
    });

    testWidgets('сотрудник выбирает «Только смотреть» для себя: свой ключ, общий не тронут; движок берёт более строгий', (t) async {
      _phone(t);
      await t.runAsync(() => AssistantModePrefs.save(db, AssistantMode.askMoney));
      await t.pumpWidget(app(_cashier));
      await settle(t);
      await pick(t, 'Только смотреть', 'assistant.mode.u1', 'view_only');
      expect(await setting(t, 'assistant.mode.u1'), 'view_only');
      expect(await setting(t, 'assistant.mode'), 'ask_money');
      expect(await t.runAsync(() => AssistantModePrefs.load(db, 'u1')), AssistantMode.viewOnly);
      // другой сотрудник живёт по общему
      expect(await t.runAsync(() => AssistantModePrefs.load(db, 'u9')), AssistantMode.askMoney);
    });

    testWidgets('общий режим не задан: сотрудник может выбрать «Спрашивать перед деньгами» для себя, плашки «владелец задал» нет', (t) async {
      _phone(t);
      await t.pumpWidget(app(_cashier));
      await settle(t);
      expect(find.textContaining('Владелец задал', findRichText: true), findsNothing);
      await pick(t, 'Спрашивать перед деньгами', 'assistant.mode.u1', 'ask_money');
      expect(await setting(t, 'assistant.mode.u1'), 'ask_money');
      expect(await setting(t, 'assistant.mode'), isNull);
    });

    testWidgets('строгий из двух: общий «Только смотреть» + свой «Спрашивать» → действует «Только смотреть»; экран показывает его же', (t) async {
      _phone(t);
      await t.runAsync(() async {
        await AssistantModePrefs.save(db, AssistantMode.viewOnly);
        await AssistantModePrefs.save(db, AssistantMode.askMoney, userId: 'u1');
      });
      expect(await t.runAsync(() => AssistantModePrefs.load(db, 'u1')), AssistantMode.viewOnly);
      await t.pumpWidget(app(_cashier));
      await settle(t);
      expect(find.text('владелец'), findsOneWidget); // метка на «Только смотреть»
      expect(find.text('Мягче, чем задал владелец — недоступно'), findsNWidgets(2));
    });
  });

  group('статус доступа к ИИ, журнал, раскладка', () {
    testWidgets('строка статуса доступа: «лимит не задан» / «есть доступ» / «недоступен»', (t) async {
      _phone(t);
      await t.pumpWidget(app(_cashier));
      await settle(t);
      expect(find.text('ИИ доступен всем сотрудникам (лимит не задан)'), findsOneWidget);
      await t.pumpWidget(app(_cashier, policy: AiAccessPolicy.set(const [AiAccessUser('u1', 'Айгерим С.')])));
      await settle(t);
      expect(find.text('У вас есть доступ к ИИ'), findsOneWidget);
      await t.pumpWidget(app(_cashier, policy: AiAccessPolicy.set(const [AiAccessUser('u7', 'Другой')])));
      await settle(t);
      expect(find.text('ИИ вам недоступен — обратитесь к владельцу'), findsOneWidget);
    });

    testWidgets('«Действия ИИ ›» открывает журнал и показывает число записей', (t) async {
      _phone(t);
      await t.runAsync(() => AiJournal().record(AiActionRecord(at: DateTime.now(), user: 'u2', tool: 'cart_edit', callId: 'x1', said: 'добавь колу', args: const {}, status: 'ok', say: 'В чек: Кола')));
      await t.pumpWidget(app(_owner));
      await settle(t);
      expect(find.text('1'), findsOneWidget);
      await t.tap(find.text('Действия ИИ'));
      await settle(t);
      expect(pushed, ['/ai/log']);
    });

    for (final lang in ['ru', 'ky']) {
      for (final scale in [1.0, 1.3]) {
        for (final dark in [false, true]) {
          for (final who in ['owner', 'cashier']) {
            testWidgets('раскладка без переполнения: $lang, шрифт $scale, тёмная=$dark, $who', (t) async {
              _phone(t, scale: scale);
              await t.runAsync(() => AssistantModePrefs.save(db, AssistantMode.askMoney));
              await t.pumpWidget(app(who == 'owner' ? _owner : _cashier, lang: lang, dark: dark));
              await settle(t);
              expect(t.takeException(), isNull);
              final l = lookupAppLocalizations(Locale(lang));
              // карточки строятся лениво: к каждой — прокруткой
              for (final title in [l.aiModeDo, l.aiModeAskMoney, l.aiModeWatch]) {
                await t.scrollUntilVisible(find.text(title), 200, scrollable: find.byType(Scrollable).first);
                expect(t.getSize(card(title)).height, greaterThanOrEqualTo(72), reason: title);
              }
              await t.scrollUntilVisible(find.text(l.aiLogTitle), 300, scrollable: find.byType(Scrollable).first);
              await t.pumpAndSettle();
              expect(t.takeException(), isNull);
              // «Действия ИИ» — кнопка не ниже 48 dp
              expect(t.getSize(card(l.aiLogTitle)).height, greaterThanOrEqualTo(48));
            });
          }
        }
      }
    }
  });

  group('голосом режим не меняется; маршруты и реестр', () {
    test('в инструментах звонка нет ни одного про режим помощника; запись настройки есть только в экране', () {
      final reg = ToolRegistry();
      registerActionTools(reg, ActionKit(host: FakeHost()));
      final names = reg.forSession(_owner).map((t) => t.name).toList();
      expect(names.where((n) => n.contains('mode') || n.contains('assistant') || n.contains('setting')), ['ui_settings']); // тема и язык — не режим помощника
      final ui = reg.forSession(_owner).firstWhere((t) => t.name == 'ui_settings');
      expect(ui.decl.toJson().toString(), isNot(contains('mode')));
      for (final t in reg.forSession(_owner)) {
        final params = (t.decl.toJson()['parameters'] as Map?)?['properties'] as Map? ?? const {};
        expect(params.keys.where((k) => k == 'assistant_mode' || k == 'mode' || k == 'assistant'), isEmpty, reason: t.name);
      }
      // сторожок по исходникам: режим сохраняет только экран «Помощник» (assistant_settings.dart)
      final offenders = <String>[];
      for (final f in Directory('lib').listSync(recursive: true).whereType<File>().where((f) => f.path.endsWith('.dart'))) {
        final text = f.readAsStringSync();
        if (text.contains('AssistantModePrefs.save') && !f.path.replaceAll('\\', '/').endsWith('features/assistant/assistant_settings.dart')) offenders.add(f.path);
      }
      expect(offenders, isEmpty, reason: 'режим помощника пишется не только из экрана настроек: $offenders');
    });

    test('экраны «Помощник» и «Действия ИИ» — настоящие маршруты, открыты всем ролям', () {
      const screens = ScreenRegistry();
      for (final key in ['ai_log', 'assistant_settings']) {
        final d = screens.all.firstWhere((d) => d.key == key);
        expect(d.exists, isTrue, reason: key);
        expect(d.allowedFor(_cashier), isTrue, reason: key);
        expect(d.allowedFor(_owner), isTrue, reason: key);
      }
      final app = File('lib/app.dart').readAsStringSync();
      expect(app, contains("path: '/ai/settings'"));
      expect(app, contains("path: '/ai/log'"));
      expect(_cashier.canRoute('/ai/settings'), isTrue);
      expect(_cashier.canRoute('/ai/log'), isTrue);
    });

    test('в журнале аудита есть вид «ИИ»', () {
      expect(Audit.kinds, contains('ai'));
    });
  });
}
