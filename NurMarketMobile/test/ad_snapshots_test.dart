// 2026-10-29 (рекламный ролик): снимки 4 экранов мобилки на телефоне 360x780 dp, плотность 3 (1080x2340 px), светлая тема,
// ru / ky / uz. Данные — выдуманные (магазин «Нур», Winston Blue, Адыгене 1,5 л, ASU, Вода питьевая 1,5 л). Сеть, камера,
// микрофон и боевая база не трогаются.
// Без --dart-define=SNAP_DIR=<папка> тест пропускается, в репозиторий ничего не пишется.
// Запуск: flutter test test/ad_snapshots_test.dart --dart-define=SNAP_DIR=<папка>
import 'dart:convert';
import 'dart:io';
import 'dart:ui' as ui;

import 'package:flutter/material.dart';
import 'package:flutter/rendering.dart';
import 'package:flutter/services.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_riverpod/misc.dart' show Override;
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/ai/assistant/action_texts.dart';
import 'package:nurmarket/core/ai/assistant/ai_journal.dart';
import 'package:nurmarket/core/ai/assistant/intent_gate.dart';
import 'package:nurmarket/core/ai/assistant/tool_registry.dart';
import 'package:nurmarket/core/ai/live_tools.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/link/kassa_link.dart';
import 'package:nurmarket/core/theme/theme.dart';
import 'package:nurmarket/core/theme/tokens.dart';
import 'package:nurmarket/core/util/audit.dart';
import 'package:nurmarket/core/util/ui.dart';
import 'package:nurmarket/features/admin/analytics.dart';
import 'package:nurmarket/features/assistant/call_overlay.dart';
import 'package:nurmarket/features/scan/cam_label.dart';
import 'package:nurmarket/features/scan/kassa_scanner_mode.dart';
import 'package:nurmarket/features/scan/scan_animations.dart';
import 'package:nurmarket/features/scan/scan_screen.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

import 'support/call_rig.dart';

const _dir = String.fromEnvironment('SNAP_DIR');
const _langs = ['ru', 'ky', 'uz'];

Future<void> _fonts() async {
  final m = FontLoader('Montserrat');
  for (final w in ['Regular', 'Medium', 'SemiBold', 'Bold', 'ExtraBold']) {
    m.addFont(rootBundle.load('assets/fonts/Montserrat-$w.ttf'));
  }
  await m.load();
  // Значки Material и Roboto берутся из Flutter SDK: кнопки со своим textStyle без семейства на телефоне рисуются Roboto,
  // а в тесте без этого — квадратами (как golden_toolkit.loadAppFonts).
  final root = Platform.environment['FLUTTER_ROOT'] ?? 'C:/Users/User/flutter';
  final fonts = Directory('$root/bin/cache/artifacts/material_fonts');
  Future<void> load(String family, List<String> files) async {
    final l = FontLoader(family);
    var any = false;
    for (final f in files) {
      final file = File('${fonts.path}/$f');
      if (file.existsSync()) {
        any = true;
        final bytes = file.readAsBytesSync();
        l.addFont(Future.value(ByteData.sublistView(bytes)));
      }
    }
    if (any) await l.load();
  }

  await load('MaterialIcons', ['materialicons-regular.otf']);
  await load('Roboto', ['roboto-regular.ttf', 'roboto-medium.ttf', 'roboto-bold.ttf', 'roboto-black.ttf', 'roboto-light.ttf']);
}

/// Телефон 360x780 dp при плотности 3; сверху 28 dp под строку состояния (как на настоящем телефоне).
void _phone(WidgetTester t) {
  t.view.physicalSize = const Size(1080, 2340);
  t.view.devicePixelRatio = 3;
  t.view.padding = const FakeViewPadding(top: 84);
  t.view.viewPadding = const FakeViewPadding(top: 84);
  t.platformDispatcher.textScaleFactorTestValue = 1.0;
  final prev = FlutterError.onError;
  FlutterError.onError = (d) {
    if (d.exceptionAsString().contains('not supported by all of its localization delegates')) return; // kk/ky нет в Material
    prev?.call(d);
  };
  addTearDown(() {
    FlutterError.onError = prev;
    t.view.reset();
    t.platformDispatcher.clearTextScaleFactorTestValue();
  });
}

const _delegates = <LocalizationsDelegate<dynamic>>[
  AppLocalizations.delegate,
  GlobalMaterialLocalizations.delegate,
  GlobalWidgetsLocalizations.delegate,
  GlobalCupertinoLocalizations.delegate,
];

Widget _app(String lang, Widget home, {List<Override> overrides = const []}) => ProviderScope(
  overrides: overrides,
  child: MaterialApp(
    debugShowCheckedModeBanner: false,
    theme: buildTheme(dark: false),
    locale: Locale(lang),
    supportedLocales: AppLocalizations.supportedLocales,
    localizationsDelegates: _delegates,
    home: home,
  ),
);

/// Текст без семейства шрифта (кнопки со своим textStyle) в тесте рисуется тестовым шрифтом-квадратами, а на телефоне это Roboto:
/// такому тексту проставляем Roboto и перекладываем кадр.
void _fixDefaultFont(RenderObject ro) {
  if (ro is RenderParagraph) {
    final s = ro.text;
    if (s is TextSpan && s.style?.fontFamily == null) {
      ro.text = TextSpan(text: s.text, children: s.children, style: (s.style ?? const TextStyle()).copyWith(fontFamily: 'Roboto'), semanticsLabel: s.semanticsLabel);
    }
  }
  ro.visitChildren(_fixDefaultFont);
}

Future<void> _save(WidgetTester t, GlobalKey key, String name) async {
  debugDisableShadows = false; // в тестах тени по умолчанию рисуются жёсткими пятнами
  _fixDefaultFont(RendererBinding.instance.renderView);
  await t.pump(const Duration(milliseconds: 16));
  await t.runAsync(() async {
    final b = key.currentContext!.findRenderObject()! as RenderRepaintBoundary;
    final img = await b.toImage(pixelRatio: 3);
    final png = await img.toByteData(format: ui.ImageByteFormat.png);
    File('$_dir/$name.png').writeAsBytesSync(png!.buffer.asUint8List());
  });
  debugDisableShadows = true;
}

/// Тёмный нейтральный фон на месте живого кадра камеры (в тесте камеры нет): без надписей и предметов.
Widget _fakeCamera() => const DecoratedBox(
  decoration: BoxDecoration(
    gradient: RadialGradient(center: Alignment(0, -.2), radius: 1.1, colors: [Color(0xFF2A4256), Color(0xFF14232F), Color(0xFF071824)]),
  ),
  child: SizedBox.expand(),
);

// ===================================================================================================== 1. главная: сводка

DateTime get _today => DateUtils.dateOnly(DateTime.now());

/// Фейковые отчёты сервера: выручка за день 48 750 сом, 63 чека, прибыль 11 900; прошлый день — чуть меньше (для «Сравнить»).
class _Repo extends AnalyticsRepository {
  _Repo() : super(ApiClient(SessionStore()));

  static const _week = [41200.0, 38900.0, 52300.0, 44100.0, 58600.0, 43100.0, 48750.0];

  @override
  Future<SalesSummary> sales(DateTime from, DateTime to) async {
    final today = _today;
    final days = [for (var i = 0; i < 7; i++) (today.subtract(Duration(days: 6 - i)), _week[i])];
    if (from == to && from != today) {
      return SalesSummary(43100, 58, 743, 10400, [(from, 43100)], const {'cash': 27000, 'card': 11800, 'transfer': 4300});
    }
    return SalesSummary(48750, 63, 774, 11900, from == to ? [(today, 48750)] : days, const {'cash': 29100, 'card': 14200, 'transfer': 5450});
  }

  @override
  Future<List<ProductSales>> products(DateTime from, DateTime to) async => const [
    ProductSales('Winston Blue', 62, 9800, 120),
    ProductSales('Адыгене 1,5 л', 130, 7150, 38),
    ProductSales('ASU', 118, 6100, 33),
    ProductSales('Вода питьевая 1,5 л', 124, 4340, 22),
    ProductSales('Хлеб белый', 150, 3000, 14),
  ];
}

// ===================================================================================================== 2. сканер для кассы

const _key = 'AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8';
final _qr = 'nurmarket-link:v1?d=KASSA-1&h=192.168.1.20&p=47830&k=$_key&n=${Uri.encodeComponent('Касса 1')}&c=1a2b3c4d';

// ===================================================================================================== 3. звонок

class _Say {
  final String prevAsk, prevAnswer, ask, aiDone, name, price;
  const _Say(this.prevAsk, this.prevAnswer, this.ask, this.aiDone, this.name, this.price);
}

const _say = {
  'ru': _Say('Сколько продали сегодня?', 'Сегодня 48 750 сом, 63 чека.', 'Поставь Адыгене 1,5 литра по 55', 'Готово, Адыгене 1,5 л теперь 55 сом.', 'Адыгене 1,5 л', '55 сом'),
  'ky': _Say('Бүгүн канча сатылды?', 'Бүгүн 48 750 сом, 63 чек.', 'Адыгене 1,5 литрдин баасын 55 кыл', 'Даяр, Адыгене 1,5 л азыр 55 сом.', 'Адыгене 1,5 л', '55 сом'),
  'uz': _Say('Bugun qancha sotildi?', "Bugun 48 750 so'm, 63 ta chek.", 'Adigene 1,5 litr narxini 55 qil', "Tayyor, Adigene 1,5 l endi 55 so'm.", 'Adigene 1,5 l', "55 so'm"),
};

void main() {
  final shot = GlobalKey();

  setUpAll(() {
    if (_dir.isNotEmpty) Directory(_dir).createSync(recursive: true);
  });

  for (final lang in _langs) {
    // ------------------------------------------------------------------------------------------ 1
    testWidgets('реклама: главная со сводкой ($lang)', (t) async {
      await t.runAsync(_fonts);
      _phone(t);
      await t.pumpWidget(RepaintBoundary(key: shot, child: _app(lang, const SummaryScreen(), overrides: [analyticsRepoProvider.overrideWithValue(_Repo())])));
      await t.pump(const Duration(milliseconds: 300));
      await t.pump(const Duration(milliseconds: 300));
      // «Сравнить» — на плитках появляются дельты к прошлому периоду
      final l = lookupAppLocalizations(Locale(lang));
      final cmp = find.text(l.infCompare);
      if (cmp.evaluate().isNotEmpty) {
        await t.tap(cmp.first);
        await t.pump(const Duration(milliseconds: 300));
        await t.pump(const Duration(milliseconds: 600));
      }
      expect(t.takeException(), isNull);
      await _save(t, shot, 'mobile-home-kpi_$lang');
      // та же сводка, прокрученная ниже: график по дням и способы оплаты
      await t.drag(find.byType(ListView).first, const Offset(0, -560));
      await t.pump(const Duration(milliseconds: 600));
      await _save(t, shot, 'mobile-home-kpi-charts_$lang');
    }, skip: _dir.isEmpty);

    // ------------------------------------------------------------------------------------------ 2
    testWidgets('реклама: сканер для кассы ($lang)', (t) async {
      await t.runAsync(_fonts);
      _phone(t);
      TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger.setMockMethodCallHandler(SystemChannels.platform, (c) async => null);
      TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger.setMockMethodCallHandler(const MethodChannel('kg.nurcrm.nurmarket/beep'), (c) async => null);
      final link = KassaLink(
        store: MemoryKassaLinkStore(),
        subnets: () async => <String>[],
        retryEvery: const Duration(milliseconds: 20),
        transport: (host, port, line, c, r) async {
          final req = jsonDecode(line) as Map<String, dynamic>;
          if (req['op'] == 'hello') return jsonEncode({'ok': true, 'code': 'ok', 'kassa': 'Касса 1', 'company': '1a2b3c4d', 'shift_open': true});
          return jsonEncode({'ok': true, 'code': 'added', 'name': 'Адыгене 1,5 л'});
        },
      );
      final ctl = KassaScannerController(link: link);
      await t.runAsync(() async {
        await link.ensureLoaded();
        await link.pairFromQr(_qr); // сопряжение по QR
        await ctl.start();
        ctl.onCode('4870001234567'); // выдуманный штрихкод
        await Future<void>.delayed(const Duration(milliseconds: 80));
      });
      await t.pumpWidget(
        RepaintBoundary(
          key: shot,
          child: _app(
            lang,
            Scaffold(
              backgroundColor: const Color(0xFF071824),
              body: Stack(
                fit: StackFit.expand,
                children: [
                  _fakeCamera(),
                  const Align(alignment: Alignment(0, -.1), child: ScanFrame()),
                  Align(
                    alignment: Alignment.topCenter,
                    child: SafeArea(
                      child: Padding(
                        padding: const EdgeInsets.all(14),
                        child: ScanModeSelect(mode: ScanMode.sale, kassa: true, onSelect: (_) {}, onKassa: () {}),
                      ),
                    ),
                  ),
                  Align(alignment: Alignment.bottomCenter, child: KassaScannerPanel(controller: ctl, onSetup: () {})),
                ],
              ),
            ),
          ),
        ),
      );
      await t.pump(const Duration(milliseconds: 400));
      expect(t.takeException(), isNull);
      expect(ctl.lastStatus, KassaScanStatus.added);
      await _save(t, shot, 'mobile-scanner-for-kassa_$lang');
      await t.pumpWidget(const SizedBox());
      ctl.dispose();
      link.dispose();
    }, skip: _dir.isEmpty);

    // ------------------------------------------------------------------------------------------ 3
    testWidgets('реклама: звонок Нур Советника ($lang)', (t) async {
      await t.runAsync(_fonts);
      _phone(t);
      final dir = Directory.systemTemp.createTempSync('nm_ad_call_');
      Audit.testDirectory = dir;
      AiJournal.testDirectory = dir;
      addTearDown(() {
        try {
          dir.deleteSync(recursive: true);
        } catch (_) {}
      });
      final s = _say[lang]!;
      final rig = CallRig(lang: lang, session: const Session(email: 'o', access: '', refresh: '', userId: 'u1', userName: 'Бакыт О.', role: 'owner', companyName: 'Нур', isOwner: true, perms: allPerms, cashboxId: 'cb1'));
      // то же, что CallRig.app, но без отладочной ленты DEBUG
      await t.pumpWidget(
        RepaintBoundary(
          key: shot,
          child: UncontrolledProviderScope(
            container: rig.container,
            child: MaterialApp(
              debugShowCheckedModeBanner: false,
              locale: Locale(lang),
              supportedLocales: AppLocalizations.supportedLocales,
              localizationsDelegates: callDelegates,
              theme: buildTheme(dark: false),
              navigatorKey: rig.navKey,
              builder: (context, child) => AssistantOverlay(navigatorKey: rig.navKey, child: child!),
              home: const StubScreen('Продажа'),
            ),
          ),
        ),
      );
      await rig.startCall(t);
      await rig.showFull(t);
      // «цена 50 → 55» — настоящий текст ответа инструмента set_price (action_texts.dart)
      rig.c.registry!.register(
        AssistantTool(
          decl: const ToolDecl('set_price_ad', 'ad'),
          group: ToolGroup.action,
          intentWords: Intents.price,
          run: (r) async {
            final parts = am('partPrice', lang, {'to': s.price, 'from': lang == 'uz' ? '50 so\'m' : '50 сом'});
            return ToolResult(ToolStatus.ok, am('priceSet', lang, {'name': s.name, 'parts': parts}), undo: true);
          },
        ),
      );
      // предыдущая реплика уже прозвучала, текущая — голосовая команда
      rig.socket.said(s.prevAsk);
      rig.socket.aiSaid(s.prevAnswer);
      rig.socket.turnComplete();
      await rig.settle(t);
      rig.socket.said(s.ask);
      await t.pump(const Duration(milliseconds: 40));
      rig.socket.toolCall('ad1', 'set_price_ad');
      await rig.settle(t, 8);
      rig.socket.aiSaid(s.aiDone);
      // звук ответа ИИ (тишина 1 с) — круг в состоянии «говорит»
      rig.socket.push({
        'serverContent': {
          'modelTurn': {
            'parts': [
              {
                'inlineData': {'mimeType': 'audio/pcm;rate=24000', 'data': base64Encode(List.filled(48000, 0))},
              },
            ],
          },
        },
      });
      await rig.settle(t, 8);
      await t.pump(const Duration(milliseconds: 500));
      expect(t.takeException(), isNull);
      expect(rig.socket.responseOf('ad1')?['status'], 'ok');
      expect(find.text(lookupAppLocalizations(Locale(lang)).aiUndo), findsOneWidget);
      await _save(t, shot, 'mobile-advisor-call_$lang');
      await rig.dispose(t);
      await t.pumpWidget(const SizedBox());
    }, skip: _dir.isEmpty);

    // ------------------------------------------------------------------------------------------ 4
    testWidgets('реклама: умная камера «По виду» ($lang)', (t) async {
      await t.runAsync(_fonts);
      _phone(t);
      await t.pumpWidget(RepaintBoundary(key: shot, child: _app(lang, const _SmartCamMock())));
      await t.pump(const Duration(milliseconds: 400));
      expect(t.takeException(), isNull);
      await _save(t, shot, 'mobile-smart-camera_$lang');
      await t.pumpWidget(const SizedBox());
    }, skip: _dir.isEmpty);
  }
}

/// Страница «Умная камера» (продажа «По виду») из настоящих виджетов и строк приложения. Живого видео в тесте нет — тёмный фон.
/// Саму страницу (LiveShotPage) и панель результата (smart_camera.dart, _salePanel) собрать нельзя без камеры и без правок lib/:
/// панель повторена по тем же виджетам, строкам l10n и цветам; кнопка «Не тот товар» — строка camNotThis из карточки умной камеры.
class _SmartCamMock extends StatelessWidget {
  const _SmartCamMock();

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    final lang = Localizations.localeOf(context).languageCode;
    final name = switch (lang) {
      'uz' => 'Ichimlik suvi 1,5 l',
      'ky' => 'Ичүүчү суу 1,5 л',
      _ => 'Вода питьевая 1,5 л',
    };
    final som = lang == 'uz' ? 'so\'m' : 'сом';
    final panel = Container(
      decoration: BoxDecoration(color: pal.panel, borderRadius: BorderRadius.circular(20)),
      padding: const EdgeInsets.fromLTRB(16, 12, 16, 8),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Text(l.seenOnPhoto, style: const TextStyle(fontSize: 17, fontWeight: FontWeight.w800)),
              const SizedBox(width: 8),
              Text(l.scanTook('0,9'), style: TextStyle(color: pal.textSoft, fontSize: 12)),
              const Spacer(),
              const Icon(Icons.close_rounded),
            ],
          ),
          const SizedBox(height: 8),
          Text('$name · ×1 · ${l.exactMatch}', style: TextStyle(color: T.success, fontWeight: FontWeight.w700)),
          ListTile(
            contentPadding: EdgeInsets.zero,
            dense: true,
            leading: ProductThumb(name: name, size: 40),
            title: Text(name, style: const TextStyle(fontWeight: FontWeight.w800)),
            subtitle: Text('35 $som · ${l.inStock('48', switch (lang) { 'uz' => 'dona', 'ky' => 'даана', _ => 'шт' })}'),
            trailing: const Icon(Icons.add_shopping_cart_rounded),
          ),
          TextButton.icon(
            style: TextButton.styleFrom(minimumSize: const Size(48, 48), padding: EdgeInsets.zero),
            onPressed: () {},
            icon: const Icon(Icons.undo_rounded, size: 18),
            label: Text(l.camNotThis),
          ),
        ],
      ),
    );
    final size = MediaQuery.sizeOf(context);
    // как LiveShotPage при показанном результате: рамка 84 % ширины и 30 % высоты, центр на 30 % высоты
    final frame = Rect.fromCenter(center: Offset(size.width / 2, size.height * .3), width: size.width * .84, height: size.height * .3);
    return Scaffold(
      backgroundColor: Colors.black,
      body: Stack(
        fit: StackFit.expand,
        children: [
          _fakeCamera(),
          Positioned.fromRect(rect: frame, child: ScanFrame(width: frame.width, height: frame.height)),
          Align(
            alignment: Alignment.topCenter,
            child: SafeArea(
              child: Padding(
                padding: const EdgeInsets.fromLTRB(12, 8, 12, 0),
                child: Column(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    Row(
                      children: [
                        IconButton.filledTonal(onPressed: () {}, icon: const Icon(Icons.arrow_back_rounded)),
                        const SizedBox(width: 10),
                        Expanded(child: Align(alignment: Alignment.centerLeft, child: CamLabel(l.smartCam, icon: Icons.photo_camera_rounded))),
                        IconButton.filledTonal(onPressed: () {}, icon: const Icon(Icons.flash_off_rounded)),
                      ],
                    ),
                  ],
                ),
              ),
            ),
          ),
          Positioned(
            left: 16,
            right: 16,
            top: frame.bottom + 12,
            child: Column(mainAxisSize: MainAxisSize.min, children: [CamLabel(l.scanNext, size: 15, weight: FontWeight.w700, align: TextAlign.center, maxLines: 3)]),
          ),
          Align(
            alignment: Alignment.bottomCenter,
            child: SafeArea(
              top: false,
              child: Padding(
                padding: const EdgeInsets.fromLTRB(16, 0, 16, 20),
                child: Column(
                  mainAxisSize: MainAxisSize.min,
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: [
                    panel,
                    const SizedBox(height: 12),
                    FilledButton.icon(
                      style: FilledButton.styleFrom(
                        backgroundColor: T.accent,
                        foregroundColor: T.navy,
                        minimumSize: const Size.fromHeight(64),
                        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(32)),
                        textStyle: const TextStyle(fontSize: 18, fontWeight: FontWeight.w800),
                      ),
                      onPressed: () {},
                      icon: const Icon(Icons.center_focus_strong_rounded, size: 28),
                      label: Text(l.scanShoot),
                    ),
                  ],
                ),
              ),
            ),
          ),
        ],
      ),
    );
  }
}
