// 2026-10-27 (поток Р-5, протокол PhoneLink раздел 8): сообщения «Код устарел — отсканируйте снова» (stale) и «Идёт оплата — сканируйте
// после неё» (busy) в панели «Сканер для кассы»: ru / ky / uz, 360 dp, шрифт 1,0 и 1,3, светлая и тёмная — без переполнений.
import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/link/kassa_link.dart';
import 'package:nurmarket/core/theme/theme.dart';
import 'package:nurmarket/features/scan/kassa_scanner_mode.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

const _key = 'AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8';
const _qr = 'nurmarket-link:v1?d=KASSA-1&h=192.168.1.20&p=47830&k=$_key&n=%D0%9E&c=1a2b3c4d';

void main() {
  late String scanReply;

  KassaLink makeLink() => KassaLink(
    store: MemoryKassaLinkStore(),
    subnets: () async => <String>[],
    retryEvery: const Duration(seconds: 30),
    transport: (host, port, line, c, r) async {
      final req = jsonDecode(line) as Map<String, dynamic>;
      if (req['op'] == 'hello') return jsonEncode({'ok': true, 'code': 'ok', 'kassa': 'К', 'company': '1a2b3c4d', 'shift_open': true});
      return scanReply;
    },
  );

  setUp(() {
    TestWidgetsFlutterBinding.ensureInitialized();
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger.setMockMethodCallHandler(SystemChannels.platform, (c) async => null);
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger.setMockMethodCallHandler(const MethodChannel('kg.nurcrm.nurmarket/beep'), (c) async => null);
  });

  testWidgets('stale и busy: текст на ru/ky/uz виден, 360 dp, шрифт 1,3, обе темы, без переполнений', (t) async {
    t.view.physicalSize = const Size(720, 1280);
    t.view.devicePixelRatio = 2.0;
    addTearDown(() {
      t.view.resetPhysicalSize();
      t.view.resetDevicePixelRatio();
      t.platformDispatcher.clearTextScaleFactorTestValue();
    });
    final prev = FlutterError.onError;
    FlutterError.onError = (d) {
      if (d.exceptionAsString().contains('not supported by all of its localization delegates')) return;
      prev?.call(d);
    };
    addTearDown(() => FlutterError.onError = prev);
    for (final lang in ['ru', 'ky', 'uz']) {
      final l = lookupAppLocalizations(Locale(lang));
      for (final dark in [false, true]) {
        for (final scale in [1.0, 1.3]) {
          t.platformDispatcher.textScaleFactorTestValue = scale;
          for (final (code, expected) in [('stale', l.klScStale), ('busy', l.klScBusy)]) {
            scanReply = jsonEncode({'ok': false, 'code': code});
            final link = makeLink();
            final c = KassaScannerController(link: link);
            await t.runAsync(() async {
              await link.pairFromQr(_qr);
              await c.start();
              c.onCode('4870001234567');
              await Future<void>.delayed(const Duration(milliseconds: 60));
            });
            await t.pumpWidget(
              MaterialApp(
                locale: Locale(lang),
                theme: buildTheme(dark: dark),
                localizationsDelegates: const [AppLocalizations.delegate, GlobalMaterialLocalizations.delegate, GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate],
                supportedLocales: AppLocalizations.supportedLocales,
                home: Scaffold(body: Align(alignment: Alignment.bottomCenter, child: KassaScannerPanel(controller: c, onSetup: () {}))),
              ),
            );
            await t.pump();
            expect(find.text(expected), findsOneWidget, reason: '$lang $code');
            expect(t.takeException(), isNull, reason: '$lang dark=$dark x$scale $code');
            c.dispose();
            link.dispose();
          }
        }
      }
    }
  });
}
