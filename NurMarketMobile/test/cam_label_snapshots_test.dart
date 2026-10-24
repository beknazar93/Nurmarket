// 2026-10-22 (подпись «Умная камера»): снимки подписей поверх камеры — до (белый текст без подложки) и после (CamLabel)
// на светлом и тёмном «кадре», в светлой и тёмной теме, шрифт 1.0 и 1.3. Без SNAP_DIR тест пропускается.
// flutter test test/cam_label_snapshots_test.dart --dart-define=SNAP_DIR=C:/Users/User/NurMarket-test/stands/mobile-label
import 'dart:io';
import 'dart:ui' as ui;

import 'package:flutter/material.dart';
import 'package:flutter/rendering.dart';
import 'package:flutter/services.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/theme/theme.dart';
import 'package:nurmarket/features/scan/cam_label.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

const _dir = String.fromEnvironment('SNAP_DIR');

Future<void> _fonts() async {
  final m = FontLoader('Montserrat');
  for (final w in ['Regular', 'Medium', 'SemiBold', 'Bold', 'ExtraBold']) {
    m.addFont(rootBundle.load('assets/fonts/Montserrat-$w.ttf'));
  }
  await m.load();
  final root = Platform.environment['FLUTTER_ROOT'] ?? 'D:/flutter';
  final icons = File('$root/bin/cache/artifacts/material_fonts/materialicons-regular.otf');
  if (icons.existsSync()) {
    await (FontLoader('MaterialIcons')..addFont(Future.value(ByteData.sublistView(icons.readAsBytesSync())))).load();
  }
}

Widget _screen(AppLocalizations l, {required bool after, required bool lightFrame}) {
  final bg = lightFrame ? const Color(0xFFEDE8DC) : const Color(0xFF14181C);
  Widget title = after
      ? CamLabel(l.smartCam, icon: Icons.photo_camera_rounded)
      : const Text('Умная камера', style: TextStyle(color: Colors.white, fontWeight: FontWeight.w800, fontSize: 18));
  Widget hint = after
      ? CamLabel(l.scanHint, size: 15, weight: FontWeight.w700, align: TextAlign.center, maxLines: 3)
      : Text(l.scanHint, textAlign: TextAlign.center, style: const TextStyle(color: Colors.white70, fontSize: 13));
  final tabs = [l.toCheck, l.byLook, l.weighShort, l.checkPrice, l.receiving];
  return Scaffold(
    backgroundColor: bg,
    body: SafeArea(
      child: Padding(
        padding: const EdgeInsets.all(12),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Row(children: [Expanded(child: Align(alignment: Alignment.centerLeft, child: title))]),
            const SizedBox(height: 10),
            Container(
              padding: const EdgeInsets.all(4),
              decoration: BoxDecoration(color: after ? camShade : const Color(0x33FFFFFF), borderRadius: BorderRadius.circular(14)),
              child: Row(children: [
                for (var i = 0; i < tabs.length; i++)
                  Expanded(
                    child: Container(
                      constraints: after ? const BoxConstraints(minHeight: 48) : null,
                      padding: EdgeInsets.symmetric(vertical: after ? 6 : 9, horizontal: 2),
                      decoration: BoxDecoration(color: i == 1 ? const Color(0xFFFFD600) : Colors.transparent, borderRadius: BorderRadius.circular(10)),
                      child: Center(
                        heightFactor: 1,
                        child: after ? FittedBox(fit: BoxFit.scaleDown, child: Text(tabs[i], maxLines: 1, style: TextStyle(fontSize: 13, fontWeight: FontWeight.w700, color: i == 1 ? const Color(0xFF0B2A3C) : Colors.white))) : Text(tabs[i], maxLines: 1, overflow: TextOverflow.ellipsis, textAlign: TextAlign.center, style: TextStyle(fontSize: 12, fontWeight: FontWeight.w700, color: i == 1 ? const Color(0xFF0B2A3C) : Colors.white)),
                      ),
                    ),
                  ),
              ]),
            ),
            const SizedBox(height: 120),
            hint,
          ],
        ),
      ),
    ),
  );
}

void main() {
  for (final after in [false, true]) {
    for (final dark in [false, true]) {
      for (final big in [false, true]) {
        for (final light in [true, false]) {
          final name = '${after ? 'after' : 'before'}_${dark ? 'dark' : 'light'}theme_${big ? 'font13' : 'font10'}_${light ? 'lightframe' : 'darkframe'}';
          testWidgets(name, (t) async {
            if (_dir.isEmpty) return;
            await t.runAsync(_fonts);
            t.view.physicalSize = const Size(780, 1000);
            t.view.devicePixelRatio = 2;
            addTearDown(t.view.reset);
            final key = GlobalKey();
            await t.pumpWidget(RepaintBoundary(
              key: key,
              child: MaterialApp(
                debugShowCheckedModeBanner: false,
                theme: buildTheme(dark: false),
                darkTheme: buildTheme(dark: true),
                themeMode: dark ? ThemeMode.dark : ThemeMode.light,
                locale: const Locale('ru'),
                supportedLocales: AppLocalizations.supportedLocales,
                localizationsDelegates: const [AppLocalizations.delegate, GlobalMaterialLocalizations.delegate, GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate],
                builder: (c, child) => MediaQuery(data: MediaQuery.of(c).copyWith(textScaler: TextScaler.linear(big ? 1.3 : 1.0)), child: child!),
                home: Builder(builder: (c) => _screen(AppLocalizations.of(c)!, after: after, lightFrame: light)),
              ),
            ));
            await t.pump();
            expect(t.takeException(), isNull);
            await t.runAsync(() async {
              final b = key.currentContext!.findRenderObject()! as RenderRepaintBoundary;
              final img = await b.toImage(pixelRatio: 1);
              final png = await img.toByteData(format: ui.ImageByteFormat.png);
              Directory(_dir).createSync(recursive: true);
              File('$_dir/$name.png').writeAsBytesSync(png!.buffer.asUint8List());
            });
          });
        }
      }
    }
  }
}
