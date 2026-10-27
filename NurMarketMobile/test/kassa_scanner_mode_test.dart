// 2026-10-27: режим «Сканер для кассы» и экран «Связь с кассой» — антидребезг 1,5 с, счётчик, панель состояния
// (ru/ky/uz, светлая и тёмная, 360 dp, шрифт 1,0 и 1,3: ничего не переполняется, кнопки не меньше 48 dp).
// Сеть и база не трогаются — касса подставная, хранилище в памяти.
import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/link/kassa_link.dart';
import 'package:nurmarket/core/providers.dart';
import 'package:nurmarket/core/theme/theme.dart';
import 'package:nurmarket/features/profile/kassa_link_screen.dart';
import 'package:nurmarket/features/scan/kassa_scanner_mode.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

const _key = 'AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8';
const _qr = 'nurmarket-link:v1?d=KASSA-1&h=192.168.1.20&p=47830&k=$_key&n=%D0%9E%D1%81%D0%BD%D0%BE%D0%B2%D0%BD%D0%B0%D1%8F&c=1a2b3c4d';

class _Fixed extends SessionController {
  @override
  Session? build() => const Session(email: 'k@x.kg', access: 'a', refresh: 'r', userId: 'u1', userName: 'Айгерим Т.', role: 'cashier');
}

void main() {
  late List<String> scans;
  late bool down;
  late String scanReply;
  late String helloReply;

  KassaLink makeLink() => KassaLink(
    store: MemoryKassaLinkStore(),
    subnets: () async => <String>[],
    retryEvery: const Duration(milliseconds: 20),
    transport: (host, port, line, c, r) async {
      if (down) throw const KassaNetException('socket');
      final req = jsonDecode(line) as Map<String, dynamic>;
      if (req['op'] == 'hello') return helloReply;
      scans.add((jsonDecode(req['body'] as String) as Map)['code'] as String);
      return scanReply;
    },
  );

  setUp(() {
    scans = [];
    down = false;
    helloReply = jsonEncode({'ok': true, 'code': 'ok', 'kassa': 'Основная', 'company': '1a2b3c4d', 'shift_open': true});
    scanReply = jsonEncode({'ok': true, 'code': 'added', 'name': 'Молоко'});
    TestWidgetsFlutterBinding.ensureInitialized();
    // вибрация и звук в тесте — пустые каналы
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger.setMockMethodCallHandler(SystemChannels.platform, (c) async => null);
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger.setMockMethodCallHandler(const MethodChannel('kg.nurcrm.nurmarket/beep'), (c) async => null);
  });

  test('антидребезг: тот же код чаще 1,5 с не уходит; другой код — сразу; счётчик и статус по ответу', () async {
    final link = makeLink();
    await link.pairFromQr(_qr);
    final c = KassaScannerController(link: link);
    await c.start();
    c.onCode('4870001234567');
    c.onCode('4870001234567'); // тот же кадр сразу же — отсечён
    c.onCode('4870001234567');
    c.onCode('4600000000011');
    await Future<void>.delayed(const Duration(milliseconds: 80));
    expect(scans, ['4870001234567', '4600000000011']);
    expect(c.added, 2);
    expect(c.lastStatus, KassaScanStatus.added);
    expect(c.lastCode, '4600000000011');
    scanReply = jsonEncode({'ok': false, 'code': 'not_found'});
    c.onCode('1111111111116');
    await Future<void>.delayed(const Duration(milliseconds: 80));
    expect(c.lastStatus, KassaScanStatus.notFound);
    expect(c.added, 2);
    scanReply = jsonEncode({'ok': false, 'code': 'till_inactive'});
    c.onCode('2222222222222');
    await Future<void>.delayed(const Duration(milliseconds: 80));
    expect(c.lastStatus, KassaScanStatus.tillInactive);
    c.dispose();
    link.dispose();
  });

  test('потеря связи: статус «в очереди», после возвращения — добавлен', () async {
    final link = makeLink();
    await link.pairFromQr(_qr);
    final c = KassaScannerController(link: link);
    await c.start();
    down = true;
    c.onCode('4870001234567');
    await Future<void>.delayed(const Duration(milliseconds: 60));
    expect(c.lastStatus, KassaScanStatus.queued);
    expect(link.pendingCount, 1);
    expect(link.status, KassaLinkStatus.notFound);
    down = false;
    await Future<void>.delayed(const Duration(milliseconds: 150));
    expect(c.lastStatus, KassaScanStatus.added);
    expect(link.pendingCount, 0);
    c.dispose();
    link.dispose();
  });

  testWidgets('панель режима: все состояния, 3 языка, обе темы, шрифт 1,0 и 1,3 — без переполнений', (t) async {
    t.view.physicalSize = const Size(720, 1280); // 360×640 dp
    t.view.devicePixelRatio = 2.0;
    addTearDown(() {
      t.view.resetPhysicalSize();
      t.view.resetDevicePixelRatio();
      t.platformDispatcher.clearTextScaleFactorTestValue();
    });
    final prev = FlutterError.onError;
    FlutterError.onError = (d) {
      if (d.exceptionAsString().contains('not supported by all of its localization delegates')) return; // kk/ky нет в Material
      prev?.call(d);
    };
    addTearDown(() => FlutterError.onError = prev);

    for (final lang in ['ru', 'ky', 'uz']) {
      for (final dark in [false, true]) {
        for (final scale in [1.0, 1.3]) {
          t.platformDispatcher.textScaleFactorTestValue = scale;
          for (final mode in ['unpaired', 'online', 'notfound', 'revoked', 'queued']) {
            down = false;
            helloReply = jsonEncode({'ok': true, 'code': 'ok', 'kassa': 'Основная', 'company': '1a2b3c4d', 'shift_open': true});
            final link = makeLink();
            await t.runAsync(() async {
              await link.ensureLoaded();
              if (mode != 'unpaired') {
                if (mode == 'notfound') down = true;
                if (mode == 'revoked') helloReply = jsonEncode({'ok': false, 'code': 'revoked'});
                await link.pairFromQr(_qr);
              }
            });
            final c = KassaScannerController(link: link);
            if (mode == 'online') {
              await t.runAsync(() async {
                await c.start();
                c.onCode('4870001234567');
                await Future<void>.delayed(const Duration(milliseconds: 50));
              });
            } else if (mode == 'queued') {
              await t.runAsync(() async {
                await c.start();
                down = true;
                c.onCode('4870001234567');
                await Future<void>.delayed(const Duration(milliseconds: 50));
              });
            }
            await t.pumpWidget(
              MaterialApp(
                locale: Locale(lang),
                theme: buildTheme(dark: dark),
                localizationsDelegates: const [
                  AppLocalizations.delegate,
                  GlobalMaterialLocalizations.delegate,
                  GlobalWidgetsLocalizations.delegate,
                  GlobalCupertinoLocalizations.delegate,
                ],
                supportedLocales: AppLocalizations.supportedLocales,
                home: Scaffold(
                  body: Align(
                    alignment: Alignment.bottomCenter,
                    child: KassaScannerPanel(controller: c, onSetup: () {}),
                  ),
                ),
              ),
            );
            await t.pump();
            expect(t.takeException(), isNull, reason: '$lang dark=$dark x$scale $mode');
            if (mode == 'unpaired') {
              final btn = t.getSize(find.byType(FilledButton));
              expect(btn.height, greaterThanOrEqualTo(48));
            }
            c.dispose();
            link.dispose();
          }
        }
      }
    }
  });

  testWidgets('экран «Связь с кассой»: не подключена — кнопки не меньше 48 dp, без переполнений', (t) async {
    t.view.physicalSize = const Size(720, 1280);
    t.view.devicePixelRatio = 2.0;
    t.platformDispatcher.textScaleFactorTestValue = 1.3;
    addTearDown(() {
      t.view.resetPhysicalSize();
      t.view.resetDevicePixelRatio();
      t.platformDispatcher.clearTextScaleFactorTestValue();
    });
    await t.pumpWidget(
      ProviderScope(
        overrides: [sessionProvider.overrideWith(_Fixed.new)],
        child: MaterialApp(
          locale: const Locale('ru'),
          theme: buildTheme(dark: true),
          localizationsDelegates: const [
            AppLocalizations.delegate,
            GlobalMaterialLocalizations.delegate,
            GlobalWidgetsLocalizations.delegate,
            GlobalCupertinoLocalizations.delegate,
          ],
          supportedLocales: AppLocalizations.supportedLocales,
          home: const KassaLinkScreen(),
        ),
      ),
    );
    await t.pump(const Duration(milliseconds: 50));
    expect(t.takeException(), isNull);
    expect(find.text('Сканировать QR на кассе'), findsOneWidget);
    expect(find.text('Касса не подключена'), findsOneWidget);
    expect(t.getSize(find.byType(FilledButton).first).height, greaterThanOrEqualTo(48));
  });
}
