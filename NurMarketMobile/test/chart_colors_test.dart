// 2026-10-26, владелец: «в аналитике, сводке используй РАЗНЫЕ ЦВЕТА в диаграммах».
// Палитра диаграмм: цвета попарно различимы (обычное зрение и дальтонизм), контраст на светлом/тёмном фоне, стабильность цвета
// по ключу, сводка (способы оплаты, топ товаров, выручка по дням) — свои цвета, светлая/тёмная тема, шрифт 1,0 и 1,3.
// Сервер не трогается — запросы идут в фейк.
import 'dart:io';
import 'dart:math' as math;
import 'dart:ui' as ui;

import 'package:flutter/material.dart';
import 'package:flutter/rendering.dart';
import 'package:flutter/services.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_riverpod/misc.dart' show Override;
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/theme/theme.dart';
import 'package:nurmarket/core/theme/tokens.dart';
import 'package:nurmarket/core/ui/infographic/infographics.dart';
import 'package:nurmarket/features/admin/analytics.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

// ---------- цвет: sRGB -> Lab, CIEDE2000, симуляция дальтонизма (Machado 2009, тяжесть 1.0) ----------
List<double> _lin(Color c) {
  double f(double v) => v <= 0.04045 ? v / 12.92 : math.pow((v + 0.055) / 1.055, 2.4).toDouble();
  return [f(c.r), f(c.g), f(c.b)];
}

List<double> _mul(List<List<double>> m, List<double> v) => [for (final r in m) r[0] * v[0] + r[1] * v[1] + r[2] * v[2]];

const _cvd = {
  'protan': [
    [0.152286, 1.052583, -0.204868],
    [0.114503, 0.786281, 0.099216],
    [-0.003882, -0.048116, 1.051998],
  ],
  'deutan': [
    [0.367322, 0.860646, -0.227968],
    [0.280085, 0.672501, 0.047413],
    [-0.011820, 0.042940, 0.968881],
  ],
  'tritan': [
    [1.255528, -0.076749, -0.178779],
    [-0.078411, 0.930809, 0.147602],
    [0.004733, 0.691367, 0.303900],
  ],
};

List<double> _lab(List<double> lin) {
  final xyz = _mul(const [
    [0.4124, 0.3576, 0.1805],
    [0.2126, 0.7152, 0.0722],
    [0.0193, 0.1192, 0.9505],
  ], lin);
  final x = xyz[0] / 0.95047, y = xyz[1], z = xyz[2] / 1.08883;
  double f(double t) => t > 216 / 24389 ? math.pow(t, 1 / 3).toDouble() : (24389 / 27 * t + 16) / 116;
  return [116 * f(y) - 16, 500 * (f(x) - f(y)), 200 * (f(y) - f(z))];
}

List<double> _labOf(Color c, [String? kind]) {
  final lin = _lin(c);
  // матрица Machado работает в линейном sRGB; результат обрезаем в 0..1
  return _lab(kind == null ? lin : [for (final v in _mul(_cvd[kind]!, lin)) v.clamp(0.0, 1.0)]);
}

double _deg(double r) => r * 180 / math.pi;
double _rad(double d) => d * math.pi / 180;

double _de2000(List<double> a, List<double> b) {
  final l1 = a[0], a1 = a[1], b1 = a[2], l2 = b[0], a2 = b[1], b2 = b[2];
  final c1 = math.sqrt(a1 * a1 + b1 * b1), c2 = math.sqrt(a2 * a2 + b2 * b2);
  final cb = (c1 + c2) / 2;
  final g = 0.5 * (1 - math.sqrt(math.pow(cb, 7) / (math.pow(cb, 7) + math.pow(25, 7))));
  final a1p = (1 + g) * a1, a2p = (1 + g) * a2;
  final c1p = math.sqrt(a1p * a1p + b1 * b1), c2p = math.sqrt(a2p * a2p + b2 * b2);
  final h1 = (_deg(math.atan2(b1, a1p)) + 360) % 360, h2 = (_deg(math.atan2(b2, a2p)) + 360) % 360;
  final dl = l2 - l1, dc = c2p - c1p;
  var dh = 0.0;
  if (c1p * c2p != 0) {
    dh = h2 - h1;
    if (dh > 180) dh -= 360;
    if (dh < -180) dh += 360;
  }
  final dH = 2 * math.sqrt(c1p * c2p) * math.sin(_rad(dh / 2));
  final lb = (l1 + l2) / 2, cbp = (c1p + c2p) / 2;
  var hb = h1 + h2;
  if (c1p * c2p != 0) {
    hb = (h1 + h2) / 2;
    if ((h1 - h2).abs() > 180) hb += h1 + h2 < 360 ? 180 : -180;
  }
  final t = 1 - 0.17 * math.cos(_rad(hb - 30)) + 0.24 * math.cos(_rad(2 * hb)) + 0.32 * math.cos(_rad(3 * hb + 6)) - 0.2 * math.cos(_rad(4 * hb - 63));
  final dth = 30 * math.exp(-math.pow((hb - 275) / 25, 2));
  final rc = 2 * math.sqrt(math.pow(cbp, 7) / (math.pow(cbp, 7) + math.pow(25, 7)));
  final sl = 1 + 0.015 * math.pow(lb - 50, 2) / math.sqrt(20 + math.pow(lb - 50, 2));
  final sc = 1 + 0.045 * cbp, sh = 1 + 0.015 * cbp * t;
  final rt = -math.sin(_rad(2 * dth)) * rc;
  return math.sqrt(math.pow(dl / sl, 2) + math.pow(dc / sc, 2) + math.pow(dH / sh, 2) + rt * (dc / sc) * (dH / sh));
}

double _minPair(List<Color> cs, [String? kind]) {
  var m = 1e9;
  for (var i = 0; i < cs.length; i++) {
    for (var j = i + 1; j < cs.length; j++) {
      m = math.min(m, _de2000(_labOf(cs[i], kind), _labOf(cs[j], kind)));
    }
  }
  return m;
}

// ---------- стенд виджетов ----------
class _Offline extends ApiClient {
  _Offline() : super(SessionStore());
}

class _Repo extends AnalyticsRepository {
  final SalesSummary summary;
  final List<ProductSales> items;
  _Repo(this.summary, this.items) : super(_Offline());
  @override
  Future<SalesSummary> sales(DateTime from, DateTime to) async => summary;
  @override
  Future<List<ProductSales>> products(DateTime from, DateTime to) async => items;
}

void _phone(WidgetTester t, {double scale = 1.0}) {
  t.view.physicalSize = const Size(720, 3600); // 360×1800 dp: весь список построен сразу (ListView ленивый)
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

Widget _app(Widget home, {List<Override> overrides = const [], String lang = 'ru', bool dark = false}) => ProviderScope(
  overrides: overrides,
  child: MaterialApp(
    theme: buildTheme(dark: false),
    darkTheme: buildTheme(dark: true),
    themeMode: dark ? ThemeMode.dark : ThemeMode.light,
    locale: Locale(lang),
    supportedLocales: AppLocalizations.supportedLocales,
    localizationsDelegates: const [
      AppLocalizations.delegate,
      GlobalMaterialLocalizations.delegate,
      GlobalWidgetsLocalizations.delegate,
      GlobalCupertinoLocalizations.delegate,
    ],
    home: Scaffold(body: home),
  ),
);

List<String> _clipped(WidgetTester t) {
  final out = <String>[];
  for (final e in find.byType(RichText).evaluate()) {
    final r = e.renderObject;
    if (r is RenderParagraph && r.didExceedMaxLines) out.add(r.text.toPlainText());
  }
  return out;
}

void main() {
  setUpAll(_fonts);

  group('палитра диаграмм', () {
    test('12 цветов, все разные', () {
      expect(T.chartColors.length, 12);
      expect(T.chartColors.toSet().length, 12);
    });

    test('попарно различимы: обычное зрение ΔE2000 ≥ 10, протанопия/дейтеранопия/тританопия ≥ 5', () {
      expect(_minPair(T.chartColors), greaterThanOrEqualTo(10));
      for (final kind in _cvd.keys) {
        expect(_minPair(T.chartColors, kind), greaterThanOrEqualTo(5), reason: kind);
      }
    });

    test('первые шесть цветов различимы ещё лучше (мелкие диаграммы)', () {
      final six = T.chartColors.take(6).toList();
      expect(_minPair(six), greaterThanOrEqualTo(14));
      for (final kind in _cvd.keys) {
        expect(_minPair(six, kind), greaterThanOrEqualTo(7), reason: kind);
      }
    });

    test('контраст графики ≥ 3:1 на белом, на фоне и панели тёмной темы; текст поверх цвета ≥ 4,5:1', () {
      for (final c in T.chartColors) {
        expect(ChartColors.contrast(c, Colors.white), greaterThanOrEqualTo(3.0), reason: '$c на белом');
        expect(ChartColors.contrast(c, T.darkWindow), greaterThanOrEqualTo(3.0), reason: '$c на тёмном окне');
        expect(ChartColors.contrast(c, T.darkPanel), greaterThanOrEqualTo(3.0), reason: '$c на тёмной панели');
        expect(ChartColors.contrast(c, ChartColors.onColor(c)), greaterThanOrEqualTo(4.5), reason: '$c — текст поверх');
      }
    });

    test('цвета групп ABC (столбцы) ≥ 3:1 на белом и тёмной панели', () {
      for (final c in const [Color(0xFF16A34A), Color(0xFF3B82F6)]) {
        expect(ChartColors.contrast(c, Colors.white), greaterThanOrEqualTo(3.0));
        expect(ChartColors.contrast(c, T.darkPanel), greaterThanOrEqualTo(3.0));
      }
    });
  });

  group('цвет по ключу', () {
    test('хэш совпадает с кассой (FNV-1a по UTF-16, нижний регистр): известные ключи', () {
      // те же числа считает C# ChartPalette (проверено отдельно, касса — стенд charts-1026)
      expect(ChartColors.hashOf('cash'), 2519019730);
      expect(ChartColors.slotOf('cash'), 10);
      expect(ChartColors.slotOf('card'), 11);
      expect(ChartColors.slotOf('transfer'), 2);
      expect(ChartColors.slotOf('Вода'), 5);
      expect(ChartColors.slotOf('Хлеб белый'), 0);
      expect(ChartColors.slotOf('Молоко'), 6);
    });

    test('регистр и пробелы по краям не меняют цвет', () {
      expect(ChartColors.forKey('  Молоко '), ChartColors.forKey('молоко'));
      expect(ChartColors.forKey('CASH'), ChartColors.forKey('cash'));
    });

    test('assign не зависит от порядка строк; одинаковый товар — одинаковый цвет', () {
      final keys = ['Вода', 'Хлеб белый', 'Молоко', 'cash', 'debt', 'mixed', 'Сыр'];
      final a = ChartColors.assign(keys);
      final b = ChartColors.assign(keys.reversed.toList());
      for (var i = 0; i < keys.length; i++) {
        expect(a[i], b[keys.length - 1 - i], reason: keys[i]);
      }
      final dup = ChartColors.assign(['Вода', 'вода ']);
      expect(dup[0], dup[1]);
    });

    test('до 12 ключей — все цвета разные (в том числе при совпадении хэшей: debt и mixed)', () {
      final keys = ['debt', 'mixed', 'cash', 'card', 'transfer', 'A', 'B', 'C', 'D', 'E', 'F', 'G'];
      expect(ChartColors.assign(keys).toSet().length, 12);
      expect(ChartColors.assign(['debt', 'mixed']).toSet().length, 2);
    });

    test('больше 12 ключей — палитра идёт по кругу, ошибок нет', () {
      final keys = [for (var i = 0; i < 30; i++) 'товар $i'];
      final cs = ChartColors.assign(keys);
      expect(cs.length, 30);
      expect(cs.toSet().length, 12);
    });

    test('цвет ключа при добавлении посторонних ключей почти не меняется: без коллизий — тот же', () {
      final base = ChartColors.assign(['Вода', 'Молоко']);
      expect(base[0], ChartColors.forKey('Вода'));
      expect(base[1], ChartColors.forKey('Молоко'));
    });
  });

  group('выручка по дням', () {
    final d0 = DateTime(2026, 10, 20);
    List<(DateTime, double)> week(List<double> v) => [for (var i = 0; i < v.length; i++) (d0.add(Duration(days: i)), v[i])];

    test('лучший и слабый день', () {
      final (best, worst) = RevenueDayBars.marks(week([100, 500, 300, 0, 500, 50, 200]));
      expect(best, {1, 4});
      expect(worst, {5});
    });

    test('мало данных или все дни равны — выделений нет', () {
      for (final v in [
        [0.0, 0.0, 300.0, 0.0],
        [200.0, 200.0, 200.0],
      ]) {
        final m = RevenueDayBars.marks(week(v));
        expect(m.$1, isEmpty);
        expect(m.$2, isEmpty);
      }
      final two = RevenueDayBars.marks(week([100, 400]));
      expect(two.$1, {1});
      expect(two.$2, <int>{}, reason: 'слабый день — только от трёх дней с продажами');
    });

    // 2026-10-29 (инфографика, спека Fable): столбцы — на общем BarChartCard; касание столбца даёт карточку под диаграммой.
    for (final dark in [false, true]) {
      for (final scale in [1.0, 1.3]) {
        testWidgets('столбцы: заголовок, подсказка, легенда «Лучший день», без переполнения — тёмная=$dark, шрифт $scale', (t) async {
          _phone(t, scale: scale);
          final days = week([100, 500, 300, 40, 200, 80, 150]);
          await t.pumpWidget(_app(Padding(padding: const EdgeInsets.all(16), child: RevenueDayBars(days, today: d0.add(const Duration(days: 6)))), dark: dark));
          await t.pumpAndSettle();
          expect(t.takeException(), isNull);
          expect(_clipped(t), isEmpty);
          expect(find.textContaining('Выручка по дням'), findsOneWidget);
          expect(find.text('Лучший день'), findsOneWidget);
          expect(find.text('Нажмите на столбец — подробности'), findsOneWidget);
          // касание второго столбца (самый высокий) — карточка с датой и суммой
          final box = t.getRect(find.descendant(of: find.byType(BarChartCard), matching: find.byType(GestureDetector)).first);
          await t.tapAt(Offset(box.left + box.width / 7 * 1.5, box.top + 60));
          await t.pumpAndSettle();
          expect(find.text('21.10'), findsOneWidget);
          expect(find.text('500 сом'), findsOneWidget);
          expect(find.text('Нажмите на столбец — подробности'), findsNothing);
        });
      }
    }

    testWidgets('легенда на 5 языках', (t) async {
      _phone(t);
      const expected = {
        'ru': 'Лучший день',
        'ky': 'Эң мыкты күн',
        'en': 'Best day',
        'tr': 'En iyi gün',
        'uz': 'Eng yaxshi kun',
      };
      for (final e in expected.entries) {
        await t.pumpWidget(_app(RevenueDayBars(week([100, 500, 300, 40, 200]), today: d0.add(const Duration(days: 9))), lang: e.key));
        await t.pumpAndSettle();
        expect(find.text(e.value), findsOneWidget, reason: e.key);
        expect(t.takeException(), isNull);
      }
    });
  });

  group('экран «Сводка»', () {
    final today = DateUtils.dateOnly(DateTime.now());
    final summary = SalesSummary(
      12000,
      9,
      1333,
      3000,
      [
        for (var i = 0; i < 7; i++) (today.subtract(Duration(days: 6 - i)), const [1200.0, 3400.0, 2100.0, 0.0, 800.0, 3000.0, 1500.0][i]),
      ],
      const {'cash': 6000, 'card': 4000, 'transfer': 1500, 'debt': 500},
    );
    final items = [
      const ProductSales('Вода', 40, 3400, 40),
      const ProductSales('Хлеб белый', 60, 2400, 25),
      const ProductSales('Молоко', 20, 1800, 60),
      const ProductSales('Сыр «Российский» весовой', 3, 1500, 400),
      const ProductSales('Сок яблочный 1 л', 12, 900, 70),
      const ProductSales('Чай чёрный', 9, 700, 50),
    ];

    /// Цвета квадратиков 12×12 (легенда кольца) и полос топ-5 (высота 8) по порядку в дереве.
    List<Color> swatches(WidgetTester t, double size) => [
      for (final c in t.widgetList<Container>(find.byType(Container)))
        if (c.decoration is BoxDecoration && (c.decoration as BoxDecoration).color != null && c.constraints != null)
          if (c.constraints!.maxWidth == size && c.constraints!.maxHeight == size) (c.decoration as BoxDecoration).color!,
    ];
    List<Color> topBars(WidgetTester t) => [
      for (final c in t.widgetList<Container>(find.byType(Container)))
        if (c.decoration is BoxDecoration && c.constraints != null && c.constraints!.maxHeight == 8 && (c.decoration as BoxDecoration).color != null) (c.decoration as BoxDecoration).color!,
    ];

    for (final dark in [false, true]) {
      for (final scale in [1.0, 1.3]) {
        testWidgets('кольцо способов оплаты и топ-5 — у каждого свой цвет; без переполнения — тёмная=$dark, шрифт $scale', (t) async {
          _phone(t, scale: scale);
          t.view.physicalSize = const Size(720, 7200); // при шрифте 1,3 вся сводка длиннее 1800 dp
          await t.pumpWidget(_app(const SummaryScreen(), overrides: [analyticsRepoProvider.overrideWithValue(_Repo(summary, items))], dark: dark));
          await t.pumpAndSettle();
          expect(t.takeException(), isNull);
          expect(_clipped(t).where((s) => s.contains('Сыр') == false), isEmpty);
          // кольцо: 4 способа — 4 разных цвета палитры, по ключу
          final pay = swatches(t, 12).take(4).toList(); // дальше — квадраты карточки прибыли
          expect(pay.length, 4);
          expect(pay.toSet().length, 4, reason: 'способы оплаты — 4 разных цвета');
          expect(pay.every(T.chartColors.contains), isTrue);
          final keys = ['cash', 'card', 'transfer', 'debt'];
          expect(pay, ChartColors.assign(keys), reason: 'порядок по убыванию суммы: cash 6000, card 4000, transfer 1500, debt 500');
          // топ-5 (из 6 товаров показаны пять), цвета по названию
          final prod = topBars(t);
          expect(prod.length, 5);
          expect(prod.toSet().length, 5, reason: 'товары — 5 разных цветов');
          expect(prod.every(T.chartColors.contains), isTrue);
          expect(prod[0], ChartColors.assign(['Вода', 'Хлеб белый', 'Молоко', 'Сыр «Российский» весовой', 'Сок яблочный 1 л'])[0]);
          expect(find.text('Чай чёрный'), findsNothing);
          // без «Сравнить» дельты нет: четыре плитки — «— н/д»
          expect(find.text('— н/д'), findsNWidgets(4));
          // честная заметка про часы
          expect(find.textContaining('Выручка по часам', skipOffstage: false), findsOneWidget);
        });
      }
    }

    testWidgets('«Сравнить» включает дельты: «— н/д» пропадает', (t) async {
      _phone(t);
      await t.pumpWidget(_app(const SummaryScreen(), overrides: [analyticsRepoProvider.overrideWithValue(_Repo(summary, items))]));
      await t.pumpAndSettle();
      await t.tap(find.text('Сравнить с прошлым периодом'));
      await t.pumpAndSettle();
      expect(find.text('— н/д'), findsNothing);
      expect(find.text('● 0 %'), findsNWidgets(4), reason: 'прошлый период в фейке такой же — без изменений');
      expect(find.text('к вчера'), findsNWidgets(4));
      expect(t.takeException(), isNull);
    });

    testWidgets('цвет товара не меняется при другой сортировке', (t) async {
      _phone(t);
      Future<Color?> waterColor(List<ProductSales> list) async {
        await t.pumpWidget(_app(SummaryScreen(key: UniqueKey()), overrides: [analyticsRepoProvider.overrideWithValue(_Repo(summary, list))]));
        await t.pumpAndSettle();
        final order = (list.toList()..sort((a, b) => b.revenue.compareTo(a.revenue))).where((p) => p.revenue > 0).take(5).map((p) => p.name).toList();
        return topBars(t)[order.indexOf('Вода')];
      }

      final first = await waterColor(items);
      final other = await waterColor([const ProductSales('Вода', 1, 3300, 40), ...items.skip(1)]);
      expect(first, other);
    });

    testWidgets('нет продаж: пустые состояния вместо диаграмм, без ошибок', (t) async {
      _phone(t);
      const empty = SalesSummary(0, 0, 0, 0, [], {});
      await t.pumpWidget(_app(const SummaryScreen(), overrides: [analyticsRepoProvider.overrideWithValue(_Repo(empty, const []))]));
      await t.pumpAndSettle();
      expect(t.takeException(), isNull);
      expect(find.text('Нет данных за период'), findsNWidgets(2), reason: 'выручка по дням и кольцо');
      expect(find.text('За этот период продаж нет'), findsOneWidget, reason: 'топ-5');
    });
  });

  // Снимки экрана для приёмки: только если задана переменная CHART_SHOTS (папка); в обычном прогоне пропускаются.
  final shotsDir = Platform.environment['CHART_SHOTS'];
  group('снимки', () {
    final today = DateUtils.dateOnly(DateTime.now());
    final summary = SalesSummary(
      12000,
      9,
      1333,
      3000,
      [
        for (var i = 0; i < 7; i++) (today.subtract(Duration(days: 6 - i)), const [1200.0, 3400.0, 2100.0, 0.0, 800.0, 3000.0, 1500.0][i]),
      ],
      const {'cash': 6000, 'card': 4000, 'transfer': 1500, 'debt': 500},
    );
    final items = [
      const ProductSales('Вода', 40, 3400, 40),
      const ProductSales('Хлеб белый', 60, 2400, 25),
      const ProductSales('Молоко', 20, 1800, 60),
      const ProductSales('Сыр «Российский» весовой', 3, 1500, 400),
      const ProductSales('Сок яблочный 1 л', 12, 900, 70),
      const ProductSales('Чай чёрный', 9, 700, 50),
    ];
    for (final dark in [false, true]) {
      testWidgets('снимок «Сводка» тёмная=$dark', (t) async {
        _phone(t);
        final key = GlobalKey();
        await t.pumpWidget(
          RepaintBoundary(
            key: key,
            child: _app(const SummaryScreen(), overrides: [analyticsRepoProvider.overrideWithValue(_Repo(summary, items))], dark: dark),
          ),
        );
        await t.pumpAndSettle();
        await t.runAsync(() async {
          final b = key.currentContext!.findRenderObject()! as RenderRepaintBoundary;
          final img = await b.toImage(pixelRatio: 1.5);
          final data = await img.toByteData(format: ui.ImageByteFormat.png);
          File('$shotsDir/mobile-summary-${dark ? 'dark' : 'light'}.png').writeAsBytesSync(data!.buffer.asUint8List());
        });
      }, skip: shotsDir == null);
    }
  });
}
