// 2026-11-08, владелец: «Android (NurMarket владелец): сенсор откликается очень плохо».
// Проверяется: (1) рябь нижних вкладок рисуется НАД цветным фоном панели (прозрачный Material внутри Container), а не под ним;
// (2) каждая вкладка — цель касания не меньше 48×48 dp; (3) нажатие рисуется обычной рябью (InkRipple), без шейдера InkSparkle.
import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/theme/theme.dart';
import 'package:nurmarket/features/home/shell.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

const _delegates = <LocalizationsDelegate<dynamic>>[
  AppLocalizations.delegate,
  GlobalMaterialLocalizations.delegate,
  GlobalWidgetsLocalizations.delegate,
  GlobalCupertinoLocalizations.delegate,
];

Widget _app({required bool dark}) => MaterialApp(
  theme: buildTheme(dark: dark),
  locale: const Locale('ru'),
  supportedLocales: AppLocalizations.supportedLocales,
  localizationsDelegates: _delegates,
  home: const CashierShell(location: '/home', child: SizedBox.expand()),
);

void main() {
  for (final dark in [false, true]) {
    testWidgets('нижние вкладки: рябь над фоном панели и цель касания ≥ 48 dp (тёмная: $dark)', (tester) async {
      await tester.pumpWidget(_app(dark: dark));
      await tester.pumpAndSettle();

      final tabs = find.byType(InkWell);
      expect(tabs, findsNWidgets(5));
      for (final tab in tabs.evaluate()) {
        // Ближайший Material над вкладкой — прозрачный, и он лежит ВНУТРИ цветного Container панели (иначе фон закрывает рябь).
        final material = tester.widget<Material>(find.ancestor(of: find.byWidget(tab.widget), matching: find.byType(Material)).first);
        expect(material.type, MaterialType.transparency);
        final materialFinder = find.ancestor(of: find.byWidget(tab.widget), matching: find.byType(Material)).first;
        final panel = find.ancestor(of: materialFinder, matching: find.byWidgetPredicate((w) => w is Container && w.decoration is BoxDecoration));
        expect(panel, findsWidgets, reason: 'прозрачный Material должен быть внутри цветной панели');

        final size = tester.getSize(find.byWidget(tab.widget));
        expect(size.height, greaterThanOrEqualTo(48));
        expect(size.width, greaterThanOrEqualTo(48));
      }

      // Касание даёт видимую рябь сразу (кадр после нажатия), без ожидания.
      final gesture = await tester.startGesture(tester.getCenter(tabs.at(1)));
      await tester.pump(const Duration(milliseconds: 50));
      // Рябь именно на прозрачном Material панели (раньше она уходила на Material экрана — под цветной фон).
      final panelMaterial = find.ancestor(of: tabs.at(1), matching: find.byType(Material)).first.evaluate().first;
      final inkLayers = <RenderObject>[];
      void collect(RenderObject r) {
        if (r.runtimeType.toString() == '_RenderInkFeatures') inkLayers.add(r);
        r.visitChildren(collect);
      }

      collect(panelMaterial.findRenderObject()!);
      expect(inkLayers, isNotEmpty);
      final painting = (inkLayers.first as dynamic).debugInkFeatures as List?;
      expect(painting, isNotNull, reason: 'рябь нажатия должна появиться на прозрачном Material панели');
      expect(painting, isNotEmpty);
      await gesture.cancel();
    });
  }

  test('тема: нажатие — InkRipple (без шейдера InkSparkle)', () {
    expect(buildTheme(dark: false).splashFactory, same(InkRipple.splashFactory));
    expect(buildTheme(dark: true).splashFactory, same(InkRipple.splashFactory));
  });
}
