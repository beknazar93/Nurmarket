// 2026-10-27 (расследование «ИИ неправильно открывает разделы и долго думает»): чат Нур Советника — навигация без ИИ.
// «Открой раздел приёмки» открывается локально (ИИ не спрашивается, сводка не собирается), ответ строится из факта открытия,
// ничья даёт кнопки; приветствие — без сводки магазина; реплики навигации в историю
// для ИИ не попадают. Всё на фейках: без сети, без настоящего Gemini, без эмулятора.
import 'package:drift/drift.dart' show driftRuntimeOptions;
import 'package:drift/native.dart';
import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_riverpod/misc.dart' show Override;
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/ai/assistant/ai_journal.dart';
import 'package:nurmarket/core/ai/assistant/ai_perf.dart';
import 'package:nurmarket/core/ai/assistant/nav_tools.dart';
import 'package:nurmarket/core/ai/vision.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/providers.dart';
import 'package:nurmarket/core/theme/theme.dart';
import 'package:nurmarket/features/admin/advisor.dart';
import 'package:nurmarket/features/assistant/call_controller.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

typedef History = List<({bool user, String text})>;

class _ChatVision extends VisionService {
  final List<String> systems = [];
  final List<History> histories = [];
  _ChatVision(super.api, super.db);
  @override
  Future<bool> hasChatKey() async => true;
  @override
  Future<String> chat(String system, History history, {Duration budget = const Duration(seconds: 45)}) async {
    systems.add(system);
    histories.add(history);
    return 'Ответ ИИ';
  }
}

class _Fixed extends SessionController {
  final Session s;
  _Fixed(this.s);
  @override
  Session? build() => s;
}

class _Nav implements NavHost, NavVerifier {
  @override
  String location = '/admin/advisor';
  @override
  bool canGoBack = true;
  bool arrives = true;
  final routes = <String>[];
  @override
  Future<void> openRoute(String l) async {
    routes.add(l);
    if (arrives) location = l;
  }

  @override
  Future<bool> confirmed(String where, {Duration wait = const Duration(milliseconds: 1500)}) async => where.startsWith('/') && Uri.parse(location).path == Uri.parse(where).path;
  @override
  Future<void> openAux(String key) async {}
  @override
  Future<void> goHome() async {}
  @override
  Future<void> goBack() async {}
  @override
  Future<List<NavProduct>> findProducts({String? query, String? barcode}) async => const [];
  @override
  Future<void> openProduct(NavProduct p) async {}
  @override
  Future<bool> openCheck({String? number, bool last = false}) async => true;
}

const _all = [
  Session.viewProducts, Session.viewAnalytics, Session.viewClients, Session.viewOrders, Session.viewShifts, Session.viewSettings,
  Session.procurement, Session.supplier, Session.editPrice, Session.sale,
];
final _owner = Session(email: 'o', access: 'a', refresh: 'r', role: 'owner', isOwner: true, companyName: 'Нур', cashboxId: '1', perms: _all);

void main() {
  late AppDatabase db;
  late _Nav nav;
  late _ChatVision vision;

  setUp(() {
    driftRuntimeOptions.dontWarnAboutMultipleDatabases = true;
    db = AppDatabase(NativeDatabase.memory());
    FlutterSecureStorage.setMockInitialValues({});
    VisionService.resetForTest();
    AiJournal.resetQueueForTests();
    AiPerf.toDisk = false;
    AiPerf.echo = false;
    AiPerf.clearForTests();
    nav = _Nav();
    vision = _ChatVision(ApiClient(SessionStore()), db);
  });
  tearDown(() async {
    AiPerf.toDisk = true;
    AiPerf.echo = true;
    await db.close();
  });

  Future<void> open(WidgetTester t) async {
    t.view.physicalSize = const Size(720, 1600);
    t.view.devicePixelRatio = 2.0;
    addTearDown(() {
      t.view.resetPhysicalSize();
      t.view.resetDevicePixelRatio();
    });
    final overrides = <Override>[
      databaseProvider.overrideWithValue(db),
      visionProvider.overrideWithValue(vision),
      sessionProvider.overrideWith(() => _Fixed(_owner)),
      callControllerProvider.overrideWith((ref) {
        final c = AssistantCallController(ref.read, deps: CallDeps(nav: nav));
        ref.onDispose(c.dispose);
        return c;
      }),
    ];
    await t.pumpWidget(
      ProviderScope(
        overrides: overrides,
        child: MaterialApp(
          locale: const Locale('ru'),
          supportedLocales: AppLocalizations.supportedLocales,
          localizationsDelegates: const [AppLocalizations.delegate, GlobalMaterialLocalizations.delegate, GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate],
          theme: buildTheme(dark: false),
          home: const AdvisorScreen(),
        ),
      ),
    );
    await t.pump();
  }

  Future<void> say(WidgetTester t, String text) async {
    await t.enterText(find.byType(TextField), text);
    await t.tap(find.byIcon(Icons.arrow_upward_rounded));
    await t.pump();
    await t.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 150)));
    await t.pump(const Duration(milliseconds: 100));
  }

  test('сводка магазина нужна только для вопросов про цифры; приветствие и «спасибо» идут без неё', () {
    for (final s in ['привет', 'спасибо', 'что ты умеешь', 'hello', 'merhaba', 'rahmat']) {
      expect(advisorNeedsStoreData(s), isFalse, reason: s);
    }
    for (final s in ['сколько продали сегодня', 'что заказать у поставщика', 'привет, какая выручка', 'какие товары лучше брать на следующей неделе']) {
      expect(advisorNeedsStoreData(s), isTrue, reason: s);
    }
  });

  testWidgets('«открой раздел приёмки» -> экран открыт, ответ из факта, ИИ не спрашивался', (t) async {
    await open(t);
    await say(t, 'э-э открой раздел приёмки');
    expect(nav.routes, ['/admin/receive']);
    expect(find.text('Открыла: Склад → Приёмка с поставщиком.'), findsOneWidget);
    expect(vision.systems, isEmpty, reason: 'ИИ и сводка магазина не нужны');
    expect(AiPerf.recent.where((e) => e.kind == 'chat.nav').length, 1);
    expect(AiPerf.recent.first.tags['path'], 'local');
  });

  testWidgets('экран не появился -> «Не получилось открыть …», слова «Открыла» нет', (t) async {
    await open(t);
    nav.arrives = false;
    await say(t, 'открой ревизию');
    await t.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 1700)));
    await t.pump(const Duration(milliseconds: 100));
    expect(find.textContaining('Не получилось открыть «Склад → Ревизия»'), findsOneWidget);
    expect(find.textContaining('Открыла'), findsNothing);
    expect(vision.systems, isEmpty);
  });

  testWidgets('ничья («табель») -> кнопки Смены / Зарплата; нажатие открывает выбранное', (t) async {
    await open(t);
    await say(t, 'открой табель');
    expect(nav.routes, isEmpty);
    expect(find.text('Какой раздел открыть? Выберите кнопкой или скажите номер.'), findsOneWidget);
    expect(find.widgetWithText(ActionChip, 'Смены'), findsOneWidget);
    expect(find.widgetWithText(ActionChip, 'Зарплата'), findsOneWidget);
    await t.tap(find.widgetWithText(ActionChip, 'Зарплата'));
    await t.pump();
    await t.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 150)));
    await t.pump(const Duration(milliseconds: 100));
    expect(nav.routes, ['/admin/salary']);
    expect(find.textContaining('Открыла: Зарплата'), findsOneWidget);
    expect(vision.systems, isEmpty);
  });

  testWidgets('приветствие идёт к ИИ без сводки магазина; в подсказке — запрет говорить «открыла», история без реплик навигации', (t) async {
    await open(t);
    await say(t, 'открой склад');
    await say(t, 'привет');
    await t.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 150)));
    await t.pump(const Duration(milliseconds: 100));
    expect(vision.systems.length, 1);
    final sys = vision.systems.single;
    expect(sys, contains('В ЭТОМ ЧАТЕ у тебя нет инструментов'));
    expect(sys, contains('Данных магазина в этом сообщении нет'));
    expect(sys, isNot(contains('ДАННЫЕ МАГАЗИНА на')));
    expect(vision.histories.single.map((m) => m.text), ['привет'], reason: 'реплики навигации в историю ИИ не идут');
    expect(find.text('Ответ ИИ'), findsOneWidget);
    final llm = AiPerf.recent.where((e) => e.kind == 'chat.llm').single;
    expect(llm.tags['ctx'], 'skip');
    expect(llm.ms.keys, containsAll(['key', 'ctx', 'ask', 'total']));
  });
}
