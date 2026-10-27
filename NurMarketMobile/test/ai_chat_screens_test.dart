// 2026-10-27, владелец: «в мобилке ИИ не отвечает в переписке». Экраны: чат Нур Советника показывает причину (лимит с часом,
// «Google отклонил ключ», «ИИ долго не отвечает») с кнопками «Повторить» / «Настройка ИИ», индикатор не вечный (общий срок 45 с);
// экран «Ключи Gemini» показывает строку «Последняя ошибка: …». Всё на фейках — без сети.
import 'dart:async';

import 'package:drift/drift.dart' show driftRuntimeOptions;
import 'package:drift/native.dart';
import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_riverpod/misc.dart' show Override;
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/ai/ai_access.dart';
import 'package:nurmarket/core/ai/assistant/ai_journal.dart';
import 'package:nurmarket/core/ai/vision.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/providers.dart';
import 'package:nurmarket/core/theme/theme.dart';
import 'package:nurmarket/features/admin/advisor.dart';
import 'package:nurmarket/features/admin/gemini_keys_screen.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

typedef History = List<({bool user, String text})>;

class _ChatVision extends VisionService {
  final Future<String> Function(String system, History h) answer;
  final VisionException? noKey;
  int asked = 0;
  _ChatVision(super.api, super.db, this.answer, {this.noKey});
  @override
  Future<bool> hasChatKey() async => noKey == null;
  @override
  VisionException noKeyFailure({bool note = true}) => noKey ?? super.noKeyFailure(note: note);
  @override
  Future<String> chat(String system, History history, {Duration budget = const Duration(seconds: 45)}) {
    asked++;
    return answer(system, history);
  }
}

class _KeysVision extends VisionService {
  _KeysVision(super.api, super.db);
  @override
  Future<Map<String, dynamic>?> cloudSecrets() async => {'TelegramAiKey': 'AIzaAAAA1111'};
  @override
  Future<AiAccessPolicy> accessPolicy({bool force = false}) async => const AiAccessPolicy.unset();
}

class _Fixed extends SessionController {
  final Session s;
  _Fixed(this.s);
  @override
  Session? build() => s;
}

final l = lookupAppLocalizations(const Locale('ru'));

void main() {
  late AppDatabase db;

  setUp(() {
    driftRuntimeOptions.dontWarnAboutMultipleDatabases = true;
    db = AppDatabase(NativeDatabase.memory());
    FlutterSecureStorage.setMockInitialValues({});
    VisionService.resetForTest();
    AiJournal.resetQueueForTests();
  });
  tearDown(() => db.close());

  Widget host(Widget home, List<Override> overrides) => ProviderScope(
    overrides: [databaseProvider.overrideWithValue(db), ...overrides],
    child: MaterialApp(
      locale: const Locale('ru'),
      supportedLocales: AppLocalizations.supportedLocales,
      localizationsDelegates: const [AppLocalizations.delegate, GlobalMaterialLocalizations.delegate, GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate],
      theme: buildTheme(dark: false),
      home: home,
    ),
  );

  void tall(WidgetTester t) {
    t.view.physicalSize = const Size(720, 1600);
    t.view.devicePixelRatio = 2.0;
    addTearDown(() {
      t.view.resetPhysicalSize();
      t.view.resetDevicePixelRatio();
    });
  }

  Future<void> settle(WidgetTester t, bool Function() ready) async {
    for (var i = 0; i < 40 && !ready(); i++) {
      await t.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 250)));
      await t.pump();
    }
    await t.pump(const Duration(milliseconds: 100));
  }

  /// Вопрос в чат; сводка магазина ходит в сеть (в тесте её нет) — ждём настоящим временем, пока [ready].
  Future<void> ask(WidgetTester t, bool Function() ready) async {
    await t.enterText(find.byType(TextField), 'Какая выручка сегодня?');
    await t.tap(find.byIcon(Icons.arrow_upward_rounded));
    await settle(t, ready);
  }

  Future<_ChatVision> open(WidgetTester t, Future<String> Function(String, History) answer, {VisionException? noKey}) async {
    tall(t);
    final v = _ChatVision(ApiClient(SessionStore()), db, answer, noKey: noKey);
    await t.pumpWidget(host(const AdvisorScreen(), [visionProvider.overrideWithValue(v)]));
    await t.pump();
    return v;
  }

  testWidgets('ИИ не отвечает: через ~45 с — «ИИ долго не отвечает», индикатор исчез, «Повторить» работает', (t) async {
    final v = await open(t, (s, h) => Completer<String>().future);
    await ask(t, () => v.asked == 1);
    expect(find.byType(CircularProgressIndicator), findsOneWidget, reason: 'пока ждём — индикатор');
    await t.pump(const Duration(seconds: 44));
    expect(find.text(l.advisorTimeout), findsNothing);
    await t.pump(const Duration(seconds: 4));
    expect(find.text(l.advisorTimeout), findsOneWidget);
    expect(find.byType(CircularProgressIndicator), findsNothing);
    expect(find.text(l.retry), findsOneWidget);
    await t.tap(find.text(l.retry));
    await settle(t, () => v.asked == 2);
    expect(v.asked, 2, reason: '«Повторить» спрашивает ИИ снова');
    await t.pump(const Duration(seconds: 50)); // дожить до срока второй попытки — без висящих таймеров
  });

  testWidgets('лимит исчерпан: «Лимит ИИ исчерпан до HH:mm (общий с кассой и ботом)» + «Повторить» и «Настройка ИИ»', (t) async {
    final until = DateTime(2026, 10, 28, 13, 0);
    final v = await open(t, (s, h) async => throw VisionException('x', issue: VisionIssue.quota, until: until));
    await ask(t, () => find.textContaining('Лимит ИИ исчерпан').evaluate().isNotEmpty);
    expect(v.asked, 1);
    final text = l.advisorQuota(VisionService.hm(until));
    expect(find.text(text), findsOneWidget);
    expect(text, contains('общий с кассой и ботом'));
    expect(find.text(l.retry), findsOneWidget);
    expect(find.text(l.aiSettings), findsOneWidget, reason: 'поможет второй ключ — кнопка настроек есть');
    expect(find.byType(CircularProgressIndicator), findsNothing);
  });

  testWidgets('ключи есть, но Google их отклонил: «Google отклонил ключ, повтор после HH:mm» + «Настройка ИИ» вместо «Добавьте ключ ИИ»', (t) async {
    final until = DateTime(2026, 10, 27, 14, 30);
    await open(t, (s, h) async => 'ответ', noKey: VisionException('x', issue: VisionIssue.keyRejected, until: until));
    await ask(t, () => find.textContaining('Google отклонил').evaluate().isNotEmpty);
    expect(find.text(l.advisorKeyRejected(VisionService.hm(until))), findsOneWidget);
    expect(find.textContaining('Добавьте ключ'), findsNothing);
    expect(find.text(l.aiSettings), findsOneWidget);
    expect(find.text(l.retry), findsOneWidget);
  });

  testWidgets('«Ключи Gemini»: строка «Последняя ошибка: …» с кодом, моделью и хвостом ключа', (t) async {
    tall(t);
    const owner = Session(email: 'o@x', access: 'a', refresh: 'r', userId: 'OWN', userName: 'Бакыт', role: 'owner', isOwner: true, companyName: 'NBS', companyInn: '1', perms: []);
    final api = ApiClient(SessionStore())..attach(owner);
    final v = _KeysVision(api, db);
    VisionService.lastChatError = AiChatError(at: DateTime(2026, 10, 27, 14, 5), code: '403', model: 'gemini-3.5-flash-lite', keyTail: '1111', text: 'The caller does not have permission');
    await t.pumpWidget(host(const GeminiKeysScreen(), [visionProvider.overrideWithValue(v), sessionProvider.overrideWith(() => _Fixed(owner))]));
    await t.pumpAndSettle();
    final line = find.byKey(const Key('gkLastError'));
    expect(line, findsOneWidget);
    final shown = (t.widget(line) as Text).data!;
    expect(shown, startsWith('Последняя ошибка: '));
    expect(shown, contains('403'));
    expect(shown, contains('gemini-3.5-flash-lite'));
    expect(shown, contains('…1111'));
    expect(shown, isNot(contains('AIzaAAAA1111')), reason: 'сам ключ не показывается');
  });

  testWidgets('«Ключи Gemini»: ошибок не было — строки нет', (t) async {
    tall(t);
    const owner = Session(email: 'o@x', access: 'a', refresh: 'r', userId: 'OWN', userName: 'Бакыт', role: 'owner', isOwner: true, companyName: 'NBS', companyInn: '1', perms: []);
    final v = _KeysVision(ApiClient(SessionStore())..attach(owner), db);
    await t.pumpWidget(host(const GeminiKeysScreen(), [visionProvider.overrideWithValue(v), sessionProvider.overrideWith(() => _Fixed(owner))]));
    await t.pumpAndSettle();
    expect(find.byKey(const Key('gkLastError')), findsNothing);
  });
}
