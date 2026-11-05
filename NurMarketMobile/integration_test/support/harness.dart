// 2026-11-05 (E2E-тесты мобилки): запуск приложения в тесте — так же, как main(), но с базой в памяти и входом в памяти
// (защищённое хранилище телефона и настоящая база не трогаются). Адрес сервера — из NURMARKET_API_URL (--dart-define).
import 'package:drift/native.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/app.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/providers.dart';
import 'package:nurmarket/l10n/app_localizations.dart';


/// Порт поддельного сервера берётся из адреса, зашитого в приложение при сборке.
int fakeServerPort() {
  const url = ApiClient.defaultBaseUrl;
  final uri = Uri.parse(url);
  if (uri.host != '127.0.0.1' && uri.host != 'localhost') {
    throw StateError('E2E-тесты работают только с поддельным сервером на loopback. Запустите: tools/qa/mobile-e2e.ps1 '
        '(--dart-define=NURMARKET_API_URL=http://127.0.0.1:порт). Сейчас адрес: $url');
  }
  return uri.port;
}

/// Вход, который живёт только в памяти теста (вместо flutter_secure_storage).
class MemorySessionStore extends SessionStore {
  Session? saved;
  @override
  Future<Session?> load() async => saved;
  @override
  Future<void> save(Session s) async => saved = s;
  @override
  Future<void> clear() async => saved = null;
}

class Launched {
  final AppDatabase db;
  final ProviderContainer container;
  final MemorySessionStore store;
  bool closed = false;
  Launched(this.db, this.container, this.store);
  AppLocalizations get l => lookupAppLocalizations(const Locale('ru'));
}

Future<Launched> launchApp(WidgetTester t, {AppDatabase? db}) async {
  final database = db ?? AppDatabase(NativeDatabase.memory());
  final store = MemorySessionStore();
  final container = ProviderContainer(overrides: [databaseProvider.overrideWithValue(database), sessionStoreProvider.overrideWithValue(store)]);
  await container.read(prefsProvider.notifier).load();
  final signedIn = await container.read(sessionProvider.notifier).restore();
  await t.pumpWidget(UncontrolledProviderScope(container: container, child: NurMarketApp(signedIn: signedIn)));
  await t.pump(const Duration(milliseconds: 300));
  final app = Launched(database, container, store);
  // тест упал на полпути — приложение и база всё равно закрываются, следующий сценарий начинает с чистого места
  addTearDown(() async {
    try {
      await closeApp(t, app);
    } catch (_) {}
  });
  return app;
}

/// Закрыть приложение: сначала убрать дерево виджетов, потом контейнер и базу.
Future<void> closeApp(WidgetTester t, Launched app) async {
  if (app.closed) return;
  app.closed = true;
  // фоновые загрузки главной (смена, итоги) должны закончиться до закрытия контейнера — иначе «Ref после dispose» уже после теста
  try {
    await pumpUntilTrue(t, () => !app.container.read(shiftProvider).loading && !app.container.read(accountLoadProvider).loading, timeout: const Duration(seconds: 10), reason: 'конец загрузок');
  } catch (_) {
    // не дождались — закрываем как есть
  }
  await pumpFor(t, const Duration(milliseconds: 700));
  app.container.read(syncProvider).stop();
  await t.pumpWidget(const SizedBox());
  app.container.dispose();
  await app.db.close();
}

/// Ждать появления [finder] (реальное время; pumpAndSettle не годится — на экранах вечные индикаторы).
Future<void> pumpUntil(WidgetTester t, Finder finder, {Duration timeout = const Duration(seconds: 25), String? reason}) async {
  final end = DateTime.now().add(timeout);
  while (DateTime.now().isBefore(end)) {
    await t.pump(const Duration(milliseconds: 100));
    if (finder.evaluate().isNotEmpty) return;
  }
  // последний шанс и понятная ошибка с тем, что на экране
  await t.pump(const Duration(milliseconds: 100));
  if (finder.evaluate().isNotEmpty) return;
  throw TestFailure('Не дождались ${reason ?? finder} за ${timeout.inSeconds} с.\nНа экране: ${_texts()}');
}

/// Ждать, пока выполнится условие.
Future<void> pumpUntilTrue(WidgetTester t, bool Function() cond, {Duration timeout = const Duration(seconds: 25), String reason = 'условие'}) async {
  final end = DateTime.now().add(timeout);
  while (DateTime.now().isBefore(end)) {
    await t.pump(const Duration(milliseconds: 100));
    if (cond()) return;
  }
  if (cond()) return;
  throw TestFailure('Не дождались: $reason (${timeout.inSeconds} с).\nНа экране: ${_texts()}');
}

/// Выдержать паузу [d] в реальном времени с перерисовкой.
Future<void> pumpFor(WidgetTester t, Duration d) async {
  final end = DateTime.now().add(d);
  while (DateTime.now().isBefore(end)) {
    await t.pump(const Duration(milliseconds: 100));
  }
}

String _texts() {
  final out = <String>[];
  // всплывающие сообщения (SnackBar) — первыми: в них обычно причина
  for (final e in find.descendant(of: find.byType(SnackBar), matching: find.byType(Text)).evaluate()) {
    final w = e.widget as Text;
    out.add('[плашка] ${w.data ?? ''}');
  }
  for (final e in find.byType(Text).evaluate()) {
    final w = e.widget as Text;
    final s = w.data ?? w.textSpan?.toPlainText() ?? '';
    if (s.trim().isNotEmpty) out.add(s.replaceAll('\n', ' '));
    if (out.length >= 25) break;
  }
  return out.join(' | ');
}

/// Нажать и дать экрану перерисоваться.
Future<void> tapAndPump(WidgetTester t, Finder f, {Duration after = const Duration(milliseconds: 400)}) async {
  expect(f, findsWidgets, reason: 'нет элемента $f');
  await t.tap(f.first);
  await pumpFor(t, after);
}
