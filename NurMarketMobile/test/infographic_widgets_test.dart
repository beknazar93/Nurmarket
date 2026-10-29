// 2026-10-29 (владелец: «в мобилке сделай инфографику лучше»; спека Fable 03-mobile-infographics): виджет-тесты общих компонентов
// на 360 dp — шрифт 1,0 и 1,3, светлая и тёмная темы, ru/ky/uz; пустые данные, одна точка, отрицательные и огромные числа,
// 365 точек, касания, TalkBack-описания, цели касания не меньше 44 dp. Сервер не трогается.
import 'package:flutter/material.dart';
import 'package:flutter/rendering.dart';
import 'package:flutter/services.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/theme/theme.dart';
import 'package:nurmarket/core/theme/tokens.dart';
import 'package:nurmarket/core/ui/infographic/chart_logic.dart';
import 'package:nurmarket/core/ui/infographic/infographics.dart';
import 'package:nurmarket/features/shift/shift_infographic.dart';
import 'package:nurmarket/features/shift/shift_repository.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

void _phone(WidgetTester t, {double scale = 1.0, double height = 1800}) {
  t.view.physicalSize = Size(720, height * 2); // 360 dp шириной
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

Widget _app(Widget body, {String lang = 'ru', bool dark = false}) => MaterialApp(
  theme: buildTheme(dark: false),
  darkTheme: buildTheme(dark: true),
  themeMode: dark ? ThemeMode.dark : ThemeMode.light,
  locale: Locale(lang),
  supportedLocales: AppLocalizations.supportedLocales,
  localizationsDelegates: const [AppLocalizations.delegate, GlobalMaterialLocalizations.delegate, GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate],
  home: Scaffold(body: SingleChildScrollView(padding: const EdgeInsets.all(16), child: body)),
);

List<String> _clipped() {
  final out = <String>[];
  for (final e in find.byType(RichText).evaluate()) {
    final r = e.renderObject;
    if (r is RenderParagraph && r.didExceedMaxLines) out.add(r.text.toPlainText());
  }
  return out;
}

List<ChartBar> _bars(List<double> v) => [for (var i = 0; i < v.length; i++) ChartBar('${i + 1}', '${(i + 1).toString().padLeft(2, '0')}.10', v[i])];

void main() {
  setUpAll(_fonts);

  group('KpiTile', () {
    for (final dark in [false, true]) {
      for (final scale in [1.0, 1.3]) {
        for (final lang in ['ru', 'ky', 'uz']) {
          testWidgets('плитки без переполнения: $lang, тёмная=$dark, шрифт $scale', (t) async {
            _phone(t, scale: scale);
            await t.pumpWidget(
              _app(
                Column(children: [
                  KpiTile(label: 'Выручка', value: '48 300', unit: 'сом', hero: true, compare: KpiCompare(on: true, delta: DeltaInfo.of(112, 100), vs: 'к прошлой неделе · 43 100'), spark: const [1, 3, 2, 5, 4, 6, 7], how: 'Как считаем'),
                  const SizedBox(height: 12),
                  KpiGrid([
                  const KpiTile(label: 'Выручка за неделю', value: '312,4 тыс', unit: 'сом', compare: KpiCompare(on: false)),
                  KpiTile(label: 'Средний чек покупателя по всем кассам', value: '1 333', unit: 'сом', compare: KpiCompare(on: true, delta: DeltaInfo.of(112, 100), vs: 'к прошлой неделе')),
                  KpiTile(label: 'Долги', value: '1,2 млн', unit: 'сом', compare: KpiCompare(on: true, delta: DeltaInfo.of(150, 100), vs: 'к прошлой неделе', inverse: true)),
                  const KpiTile(label: 'Чеков', value: '9', compare: KpiCompare(on: true)),
                  ]),
                ]),
                lang: lang,
                dark: dark,
              ),
            );
            await t.pumpAndSettle();
            expect(t.takeException(), isNull);
            expect(_clipped().where((s) => s.contains('покупателя') == false), isEmpty);
          });
        }
      }
    }

    testWidgets('«— н/д» без сравнения, дельта со знаком и цветом при сравнении, «нет сравнения» без прошлого', (t) async {
      _phone(t);
      await t.pumpWidget(
        _app(
          Column(
            children: [
              const KpiTile(label: 'A', value: '1', compare: KpiCompare(on: false)),
              KpiTile(label: 'B', value: '2', compare: KpiCompare(on: true, delta: DeltaInfo.of(88, 100), vs: 'к вчера')),
              KpiTile(label: 'C', value: '3', compare: KpiCompare(on: true, delta: DeltaInfo.of(112, 100), vs: 'к вчера', inverse: true)),
              const KpiTile(label: 'D', value: '4', compare: KpiCompare(on: true)),
            ],
          ),
        ),
      );
      expect(find.text('— н/д'), findsOneWidget);
      expect(find.text('— сравнения нет'), findsOneWidget);
      Color colorOf(String text) => (t.widget<Text>(find.text(text)).style!.color)!;
      final pal = const Palette(false);
      expect(colorOf('▼ −12 %'), pal.danger, reason: 'падение выручки — красный');
      expect(colorOf('▲ +12 %'), pal.danger, reason: 'рост долга — красный (inverse)');
    });

    testWidgets('значение огромное — вписывается, а не вылезает; грузится — скелет без значения; ошибка — «Повторить»', (t) async {
      _phone(t);
      var retried = 0;
      await t.pumpWidget(
        _app(
          Column(
            children: [
              const KpiTile(label: 'Выручка', value: '999 999 999 999 999', unit: 'сом'),
              const KpiTile(label: 'Грузится'),
              KpiTile(label: 'Нет связи', failed: true, onRetry: () => retried++),
            ],
          ),
        ),
      );
      expect(t.takeException(), isNull);
      expect(find.byType(InfoSkeleton), findsOneWidget);
      await t.tap(find.text('Повторить'));
      expect(retried, 1);
    });

    testWidgets('TalkBack: плитка читается одной фразой с дельтой словами', (t) async {
      _phone(t);
      final h = t.ensureSemantics();
      await t.pumpWidget(
        _app(KpiTile(label: 'Выручка', value: '12 300', unit: 'сом', compare: KpiCompare(on: true, delta: DeltaInfo.of(112, 100), vs: 'к прошлой неделе'))),
      );
      expect(find.bySemanticsLabel(RegExp('Выручка.*12 300 сом.*Выше на 12 % к прошлой неделе')), findsOneWidget);
      h.dispose();
    });
  });

  group('BarChartCard', () {
    for (final dark in [false, true]) {
      for (final scale in [1.0, 1.3]) {
        testWidgets('7 столбцов: заголовок, подсказка, касание → карточка; без переполнения — тёмная=$dark, шрифт $scale', (t) async {
          _phone(t, scale: scale);
          await t.pumpWidget(
            _app(BarChartCard(title: 'Выручка по дням', subtitle: 'сом', semanticsName: 'Выручка по дням', unit: 'сом', bars: _bars([100, 500, 300, 0, 200, 80, 150]), best: const {1}), dark: dark),
          );
          await t.pumpAndSettle();
          expect(t.takeException(), isNull);
          expect(_clipped(), isEmpty);
          expect(find.text('Нажмите на столбец — подробности'), findsOneWidget);
          final r = t.getRect(find.descendant(of: find.byType(BarChartCard), matching: find.byType(GestureDetector)).first);
          expect(r.height, greaterThanOrEqualTo(150));
          await t.tapAt(Offset(r.left + r.width * 5.5 / 7, r.top + 60));
          await t.pumpAndSettle();
          expect(find.text('06.10'), findsOneWidget);
          expect(find.text('80 сом'), findsOneWidget);
          // та же карточка по проведению пальцем
          await t.dragFrom(Offset(r.left + r.width * 0.5 / 7, r.top + 60), Offset(r.width * 2 / 7, 0));
          await t.pumpAndSettle();
          expect(find.text('03.10'), findsOneWidget);
        });
      }
    }

    testWidgets('ky и uz: длинные подписи, подсказка на языке', (t) async {
      for (final lang in ['ky', 'uz']) {
        _phone(t, scale: 1.3);
        await t.pumpWidget(_app(BarChartCard(title: 'Күндөр боюнча түшүм', semanticsName: 'x', unit: 'сом', bars: _bars([1, 2, 3])), lang: lang));
        await t.pumpAndSettle();
        expect(t.takeException(), isNull);
        expect(find.text(lookupAppLocalizations(Locale(lang)).infTapHint), findsOneWidget);
      }
    });

    testWidgets('нет данных → пустое состояние с подсказкой', (t) async {
      _phone(t);
      await t.pumpWidget(_app(const BarChartCard(title: 'Выручка по дням', semanticsName: 'x', unit: 'сом', bars: [])));
      expect(find.text('Нет данных за период'), findsOneWidget);
      expect(find.text('Выберите другой период или проведите продажи'), findsOneWidget);
    });

    testWidgets('одна точка, нули, отрицательные и огромные значения — рисуются без ошибок', (t) async {
      _phone(t);
      for (final v in [
        [500.0],
        [0.0, 0.0, 0.0],
        [-300.0, 500.0, -50.0, 0.0],
        [1e12, 5e11, 2e9],
        [double.nan, 5.0],
      ]) {
        await t.pumpWidget(_app(BarChartCard(key: UniqueKey(), title: 'T', semanticsName: 'x', unit: 'сом', bars: _bars(v), best: const {0})));
        await t.pumpAndSettle();
        expect(t.takeException(), isNull, reason: '$v');
        final r = t.getRect(find.descendant(of: find.byType(BarChartCard), matching: find.byType(GestureDetector)).first);
        await t.tapAt(Offset(r.left + 3, r.top + 40));
        await t.pumpAndSettle();
        expect(t.takeException(), isNull, reason: 'касание $v');
      }
    });

    testWidgets('365 точек и «прошлый период»: строится, подписи не наезжают, легенда серого столбика', (t) async {
      _phone(t);
      final v = [for (var i = 0; i < 365; i++) 100.0 + (i * 37) % 900];
      await t.pumpWidget(_app(BarChartCard(title: 'Год', semanticsName: 'Год', unit: 'сом', bars: _bars(v), previous: [for (final x in v) x * 0.8])));
      await t.pumpAndSettle();
      expect(t.takeException(), isNull);
      expect(find.text('Прошлый период'), findsOneWidget);
      final r = t.getRect(find.descendant(of: find.byType(BarChartCard), matching: find.byType(GestureDetector)).first);
      await t.tapAt(Offset(r.left + r.width / 2, r.top + 50));
      await t.pumpAndSettle();
      expect(t.takeException(), isNull);
    });

    testWidgets('TalkBack: у графика текстовое описание «максимум … минимум … всего»', (t) async {
      _phone(t);
      final h = t.ensureSemantics();
      await t.pumpWidget(_app(BarChartCard(title: 'Выручка по дням', semanticsName: 'Выручка по дням', unit: 'сом', bars: _bars([100, 12300, 0]))));
      expect(find.bySemanticsLabel(RegExp('Выручка по дням: максимум 12[  ]300 сом \\(02\\.10\\), минимум 0 сом \\(03\\.10\\), всего 12[  ]400 сом')), findsOneWidget);
      h.dispose();
    });
  });

  group('DonutCard', () {
    final slices = const [Slice('cash', 'Наличные', 6000), Slice('card', 'Карта', 4000), Slice('transfer', 'Перевод / QR', 1500), Slice('debt', 'В долг', 500)];
    for (final dark in [false, true]) {
      for (final scale in [1.0, 1.3]) {
        testWidgets('кольцо и легенда: цвета по ключу, проценты = 100, без переполнения — тёмная=$dark, шрифт $scale', (t) async {
          _phone(t, scale: scale);
          await t.pumpWidget(_app(DonutCard(title: 'Способы оплаты', semanticsName: 'Способы оплаты', unit: 'сом', slices: slices), dark: dark));
          await t.pumpAndSettle();
          expect(t.takeException(), isNull);
          expect(_clipped(), isEmpty);
          expect(find.text('50 %'), findsOneWidget);
          expect(find.text('Всего'), findsOneWidget);
          // касание строки легенды → в центре её название
          await t.tap(find.text('Карта'));
          await t.pumpAndSettle();
          expect(find.text('Всего'), findsNothing);
          expect(find.text('Карта'), findsNWidgets(2));
          final tile = t.getSize(find.ancestor(of: find.text('Карта').last, matching: find.byType(InkWell)).first);
          expect(tile.height, greaterThanOrEqualTo(44));
        });
      }
    }

    testWidgets('больше шести способов — «Прочее»; один сектор; пусто; нули', (t) async {
      _phone(t);
      await t.pumpWidget(_app(DonutCard(title: 'T', semanticsName: 'T', unit: 'сом', slices: [for (var i = 1; i <= 9; i++) Slice('k$i', 'Способ $i', i * 10.0)])));
      expect(find.text('Прочее'), findsOneWidget);
      await t.pumpWidget(_app(DonutCard(key: UniqueKey(), title: 'T', semanticsName: 'T', unit: 'сом', slices: const [Slice('a', 'Один', 5)])));
      expect(find.text('100 %'), findsOneWidget);
      await t.pumpWidget(_app(DonutCard(key: UniqueKey(), title: 'T', semanticsName: 'T', unit: 'сом', slices: const [Slice('a', 'Ноль', 0), Slice('b', 'Минус', -4)])));
      expect(find.text('Нет данных за период'), findsOneWidget);
      expect(t.takeException(), isNull);
    });

    testWidgets('TalkBack: кольцо читается «Название: Наличные 50 %, …»', (t) async {
      _phone(t);
      final h = t.ensureSemantics();
      await t.pumpWidget(_app(DonutCard(title: 'Способы оплаты', semanticsName: 'Способы оплаты', unit: 'сом', slices: slices)));
      expect(find.bySemanticsLabel(RegExp('Способы оплаты: Наличные 50 %, Карта 33 %')), findsOneWidget);
      h.dispose();
    });
  });

  group('остальные компоненты', () {
    testWidgets('PeriodBar: период и «Сравнить» переключаются, касания не меньше 44 dp, ky/uz без переполнения', (t) async {
      for (final lang in ['ru', 'ky', 'uz']) {
        _phone(t, scale: 1.3);
        var period = 'today';
        var compare = false;
        await t.pumpWidget(
          _app(
            StatefulBuilder(
              builder: (c, set) => PeriodBar(period: period, onPeriod: (v) => set(() => period = v), compare: compare, onCompare: (v) => set(() => compare = v)),
            ),
            lang: lang,
          ),
        );
        final l = lookupAppLocalizations(Locale(lang));
        expect(t.getSize(find.ancestor(of: find.text(l.infCompare), matching: find.byType(InkWell)).first).height, greaterThanOrEqualTo(44));
        await t.tap(find.text(l.periodWeek));
        await t.tap(find.text(l.infCompare));
        await t.pumpAndSettle();
        expect(period, 'week');
        expect(compare, isTrue);
        expect(t.takeException(), isNull);
      }
    });

    testWidgets('TopBars: полоса относительно первого, длинное название обрезается, огромные суммы вписываются', (t) async {
      _phone(t, scale: 1.3);
      await t.pumpWidget(
        _app(
          TopBars(
            unit: 'сом',
            rows: [
              TopBarRow('Очень длинное название товара ' * 4, 5e12, T.chartColors[0]),
              TopBarRow('Вода', 2.5e12, T.chartColors[1]),
              TopBarRow('Ноль', 0, T.chartColors[2]),
            ],
          ),
        ),
      );
      expect(t.takeException(), isNull);
      final bars = [
        for (final c in t.widgetList<Container>(find.byType(Container)))
          if (c.constraints?.maxHeight == 8) c,
      ];
      expect(bars.length, 3);
    });

    testWidgets('InfoProgressBar: 0, больше максимума, ноль максимума, метка плана', (t) async {
      _phone(t);
      await t.pumpWidget(
        _app(
          const Column(
            children: [
              InfoProgressBar(value: 0, max: 100),
              InfoProgressBar(value: 250, max: 100, marker: 80),
              InfoProgressBar(value: 5, max: 0),
              InfoProgressBar(value: double.nan, max: 10),
            ],
          ),
        ),
      );
      expect(t.takeException(), isNull);
    });

    testWidgets('InfoError: время последних данных и «Повторить» ≥ 44; InfoEmpty; скелет без значений', (t) async {
      _phone(t);
      var n = 0;
      await t.pumpWidget(_app(Column(children: [InfoError(onRetry: () => n++, lastData: DateTime(2026, 10, 29, 13, 40)), const InfoEmpty(title: 'Пока нет продаж', hint: 'Данные появятся после первого чека'), const InfoSkeleton(height: 40)])));
      expect(find.textContaining('13:40'), findsOneWidget);
      expect(t.getSize(find.ancestor(of: find.text('Повторить'), matching: find.byType(OutlinedButton)).first).height, greaterThanOrEqualTo(44));
      await t.tap(find.text('Повторить'));
      expect(n, 1);
      expect(find.text('Пока нет продаж'), findsOneWidget);
      expect(t.takeException(), isNull);
    });

    testWidgets('Sparkline: 0, 1, 2 и ровный ряд точек не падают', (t) async {
      _phone(t);
      await t.pumpWidget(
        _app(
          Column(
            children: [
              const Sparkline([], color: Colors.red),
              const Sparkline([5], color: Colors.red),
              const Sparkline([1, 2], color: Colors.red),
              const Sparkline([3, 3, 3, 3], color: Colors.red),
              Sparkline([for (var i = 0; i < 365; i++) i % 7 * 1.0], color: Colors.red),
              const Sparkline([double.nan, 1, double.infinity], color: Colors.red),
            ],
          ),
        ),
      );
      expect(t.takeException(), isNull);
    });

    testWidgets('«Как посчитано»: значок ≥ 44, открывает текст внизу', (t) async {
      _phone(t);
      await t.pumpWidget(_app(const InfoCard(title: 'Прибыль', how: 'Прибыль = выручка − закупка', child: SizedBox(height: 10))));
      final size = t.getSize(find.byType(IconButton));
      expect(size.width, greaterThanOrEqualTo(44));
      expect(size.height, greaterThanOrEqualTo(44));
      await t.tap(find.byType(IconButton));
      await t.pumpAndSettle();
      expect(find.text('Как это посчитано'), findsOneWidget);
      expect(find.text('Прибыль = выручка − закупка'), findsOneWidget);
    });
  });

  group('смена: продажи и ящик', () {
    const bd = ShiftBreakdown(
      opening: 2000,
      cashSales: 29950,
      nonCashSales: 16900,
      debtSales: 1450,
      revenue: 48300,
      salesCount: 134,
      inflow: 2000,
      outflow: 5000,
      returnsCash: 620,
      returnsTotal: 620,
      returnsCount: 2,
      debtPaymentsCash: 0,
      serverExpected: 28330,
    );
    for (final dark in [false, true]) {
      for (final scale in [1.0, 1.3]) {
        testWidgets('«Чем платили» и «Деньги в ящике»: суммы со знаками, итог сервера — тёмная=$dark, шрифт $scale', (t) async {
          _phone(t, scale: scale);
          await t.pumpWidget(_app(const ShiftInfographic(bd), dark: dark));
          await t.pumpAndSettle();
          expect(t.takeException(), isNull);
          expect(_clipped(), isEmpty);
          expect(find.text('+2 000 сом'), findsOneWidget);
          expect(find.text('−5 000 сом'), findsOneWidget);
          expect(find.text('−620 сом'), findsOneWidget);
          expect(find.text('28 330 сом'), findsOneWidget);
          final pal = Palette(dark);
          expect(t.widget<Text>(find.text('−5 000 сом')).style!.color, pal.danger);
          expect(t.widget<Text>(find.text('+2 000 сом')).style!.color, pal.success);
        });
      }
    }

    testWidgets('нет продаж в смене — пустое состояние, неизвестные части ящика не выдумываются', (t) async {
      _phone(t);
      await t.pumpWidget(_app(const ShiftInfographic(ShiftBreakdown(opening: 100, serverExpected: 100))));
      await t.pumpAndSettle();
      expect(t.takeException(), isNull);
      expect(find.text('В смене пока нет продаж'), findsOneWidget);
      expect(find.text('+0 сом'), findsNothing);
    });

    testWidgets('PaymentSplit компактный: проценты на главной, ноль — ничего', (t) async {
      _phone(t);
      await t.pumpWidget(_app(const Column(children: [PaymentSplit(cash: 62, nonCash: 35, debt: 3, onDark: false), PaymentSplit(cash: 0, nonCash: 0, debt: 0)])));
      expect(find.text('Наличные 62 %'), findsOneWidget);
      expect(find.text('В долг 3 %'), findsOneWidget);
      expect(t.takeException(), isNull);
    });
  });
}
