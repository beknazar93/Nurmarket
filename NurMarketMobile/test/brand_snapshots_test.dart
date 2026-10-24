// 2026-10-22, брендбук NUR CRM: снимки знака (4 варианта, размеры 28–128), паттерна и шапки входа в светлой и тёмной теме.
// Без --dart-define=SNAP_DIR=<папка> тест пропускается, в репозиторий ничего не пишется.
import 'dart:io';
import 'dart:ui' as ui;

import 'package:flutter/material.dart';
import 'package:flutter/rendering.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/theme/brand_mark.dart';
import 'package:nurmarket/core/theme/theme.dart';
import 'package:nurmarket/core/theme/tokens.dart';

const _dir = String.fromEnvironment('SNAP_DIR');

Future<void> _fonts() async {
  final m = FontLoader('Montserrat');
  for (final w in ['Regular', 'Medium', 'SemiBold', 'Bold', 'ExtraBold']) {
    m.addFont(rootBundle.load('assets/fonts/Montserrat-$w.ttf'));
  }
  await m.load();
}

void main() {
  final shot = GlobalKey();
  for (final dark in [false, true]) {
    final sfx = dark ? 'dark' : 'light';
    testWidgets('снимок брендбука: знак, паттерн, шапка входа ($sfx)', (t) async {
      await t.runAsync(_fonts);
      t.view.physicalSize = const Size(780, 1500);
      t.view.devicePixelRatio = 2;
      addTearDown(t.view.reset);
      final p = Palette(dark);
      Widget cell(Color bg, BrandVariant v, {Color? pat}) => Container(
        width: 110,
        height: 110,
        color: bg,
        child: Stack(children: [
          if (pat != null) Positioned.fill(child: BrandPattern(color: pat.withValues(alpha: .25), cell: 28)),
          Center(child: BrandMark(size: 64, variant: v)),
        ]),
      );
      await t.pumpWidget(RepaintBoundary(
        key: shot,
        child: MaterialApp(
          debugShowCheckedModeBanner: false,
          theme: buildTheme(dark: dark),
          home: Scaffold(
            body: ListView(children: [
              Container(
                width: double.infinity,
                decoration: const BoxDecoration(color: T.navy, borderRadius: BorderRadius.vertical(bottom: Radius.circular(28))),
                padding: const EdgeInsets.fromLTRB(24, 48, 24, 28),
                child: const Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                  BrandLogo(width: 130, kind: BrandLogoKind.vertical, variant: BrandVariant.dark),
                  SizedBox(height: 14),
                  Text('NurMarket', style: TextStyle(fontSize: 26, fontWeight: FontWeight.w800, color: Colors.white)),
                  SizedBox(height: 6),
                  Text('Касса и склад в телефоне · Касса жана склад телефондо ү ө ң', style: TextStyle(fontSize: 13, color: Color(0xFFC7D2DB))),
                ]),
              ),
              Padding(
                padding: const EdgeInsets.all(16),
                child: Wrap(spacing: 8, runSpacing: 8, children: [
                  cell(Colors.white, BrandVariant.light),
                  cell(T.navy, BrandVariant.dark, pat: T.accent),
                  cell(T.accent, BrandVariant.yellow, pat: Colors.white),
                  cell(Colors.white, BrandVariant.mono),
                  BrandLogo(width: 150, kind: BrandLogoKind.vertical),
                ]),
              ),
              Padding(
                padding: const EdgeInsets.symmetric(horizontal: 16),
                child: Row(children: [
                  for (final s in [50.0, 60]) ...[BrandLogo(width: s * 2, kind: BrandLogoKind.horizontal), const SizedBox(width: 16)],
                ]),
              ),
              Padding(
                padding: const EdgeInsets.all(16),
                child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                  FilledButton(onPressed: () {}, child: const Text('Оплатить · Төлөө')),
                  const SizedBox(height: 10),
                  OutlinedButton(onPressed: () {}, child: const Text('Отмена')),
                  const SizedBox(height: 10),
                  Container(padding: const EdgeInsets.all(14), decoration: BoxDecoration(color: p.panel, borderRadius: BorderRadius.circular(16), border: Border.all(color: p.border)), child: Text('Итого 1 234 567 сом', style: TextStyle(fontWeight: FontWeight.w800, fontSize: 22, color: p.text))),
                ]),
              ),
            ]),
          ),
        ),
      ));
      await t.pumpAndSettle();
      await t.runAsync(() async {
        final b = shot.currentContext!.findRenderObject()! as RenderRepaintBoundary;
        final img = await b.toImage(pixelRatio: 1);
        final png = await img.toByteData(format: ui.ImageByteFormat.png);
        File('$_dir/brand_$sfx.png').writeAsBytesSync(png!.buffer.asUint8List());
      });
    }, skip: _dir.isEmpty);
  }
}
