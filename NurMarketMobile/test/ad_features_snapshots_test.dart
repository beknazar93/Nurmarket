// 2026-10-29 (рекламные ролики): кадры функций мобилки на телефоне 360x780 dp, плотность 3 (1080x2340 px), светлая тема.
// Данные выдуманные (магазин «Нур», Нан 30, Адыгене 1,5 л 55, сотрудник Айнура). Сеть, камера, микрофон и боевая база не трогаются.
// Без --dart-define=SNAP_DIR=<папка> тест пропускается, в репозиторий ничего не пишется.
// Запуск: flutter test test/ad_features_snapshots_test.dart --dart-define=SNAP_DIR=<папка>
// PNG-снимки кладутся в <папка>; для видео — кадры в <папка>/_fr_<имя>/ + list.txt (12 к/с, склеивает ffmpeg вне теста).
import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:ui' as ui;

import 'package:drift/drift.dart' show Value;
import 'package:drift/native.dart';
import 'package:flutter/material.dart';
import 'package:flutter/rendering.dart';
import 'package:flutter/services.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_riverpod/misc.dart' show Override;
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';
import 'package:nurmarket/core/ai/ai_access.dart';
import 'package:nurmarket/core/ai/assistant/ai_journal.dart';
import 'package:nurmarket/core/ai/assistant/intent_gate.dart';
import 'package:nurmarket/core/ai/assistant/journal_undo.dart';
import 'package:nurmarket/core/ai/assistant/tool_registry.dart';
import 'package:nurmarket/core/ai/live_tools.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/link/kassa_link.dart';
import 'package:nurmarket/core/providers.dart';
import 'package:nurmarket/core/theme/theme.dart';
import 'package:nurmarket/core/theme/tokens.dart';
import 'package:nurmarket/core/util/audit.dart';
import 'package:nurmarket/core/util/ui.dart';
import 'package:nurmarket/features/admin/analytics.dart';
import 'package:nurmarket/features/assistant/ai_log_screen.dart';
import 'package:nurmarket/features/assistant/call_overlay.dart';
import 'package:nurmarket/features/home/home_screen.dart';
import 'package:nurmarket/features/profile/kassa_link_screen.dart';
import 'package:nurmarket/features/scan/cam_label.dart';
import 'package:nurmarket/features/scan/kassa_scanner_mode.dart';
import 'package:nurmarket/features/scan/scan_animations.dart';
import 'package:nurmarket/features/scan/scan_screen.dart';
import 'package:nurmarket/features/shift/shift_repository.dart' show ShiftInfo;
import 'package:nurmarket/features/staff/staff_providers.dart' show myTasksProvider;
import 'package:nurmarket/features/stock/product_edit_repository.dart' show ProductDraft;
import 'package:nurmarket/l10n/app_localizations.dart';

import 'action_tools_test.dart' show Rig;
import 'support/call_rig.dart';

const _dir = String.fromEnvironment('SNAP_DIR');

// ---------------------------------------------------------------------------------------------------------------- общее

Future<void> _fonts() async {
  final m = FontLoader('Montserrat');
  for (final w in ['Regular', 'Medium', 'SemiBold', 'Bold', 'ExtraBold']) {
    m.addFont(rootBundle.load('assets/fonts/Montserrat-$w.ttf'));
  }
  await m.load();
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

void _phone(WidgetTester t) {
  t.view.physicalSize = const Size(1080, 2340);
  t.view.devicePixelRatio = 3;
  t.view.padding = const FakeViewPadding(top: 84);
  t.view.viewPadding = const FakeViewPadding(top: 84);
  t.platformDispatcher.textScaleFactorTestValue = 1.0;
  final prev = FlutterError.onError;
  FlutterError.onError = (d) {
    if (d.exceptionAsString().contains('not supported by all of its localization delegates')) return;
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

Widget _app(String lang, Widget home, {List<Override> overrides = const [], GlobalKey<NavigatorState>? nav}) => ProviderScope(
  overrides: overrides,
  child: MaterialApp(
    debugShowCheckedModeBanner: false,
    navigatorKey: nav,
    theme: buildTheme(dark: false),
    locale: Locale(lang),
    supportedLocales: AppLocalizations.supportedLocales,
    localizationsDelegates: _delegates,
    home: home,
  ),
);

void _fixDefaultFont(RenderObject ro) {
  if (ro is RenderParagraph) {
    final s = ro.text;
    if (s is TextSpan && s.style?.fontFamily == null) {
      ro.text = TextSpan(text: s.text, children: s.children, style: (s.style ?? const TextStyle()).copyWith(fontFamily: 'Roboto'), semanticsLabel: s.semanticsLabel);
    }
  }
  ro.visitChildren(_fixDefaultFont);
}

/// Снимок в файл [path]. Перед снимком — 16 мс кадра (шрифты по умолчанию переложились).
Future<void> _shot(WidgetTester t, GlobalKey key, String path) async {
  debugDisableShadows = false;
  _fixDefaultFont(RendererBinding.instance.renderView);
  await t.pump(const Duration(milliseconds: 16));
  await t.runAsync(() async {
    final b = key.currentContext!.findRenderObject()! as RenderRepaintBoundary;
    final img = await b.toImage(pixelRatio: 3);
    final png = await img.toByteData(format: ui.ImageByteFormat.png);
    File(path).writeAsBytesSync(png!.buffer.asUint8List());
  });
  debugDisableShadows = true;
}

Future<void> _save(WidgetTester t, GlobalKey key, String name) => _shot(t, key, '$_dir/$name.png');

/// Запись видео: кадры раз в 1/12 с; [hold] — сколько кадров держится этот.
class _Video {
  final String name;
  final Directory dir;
  final frames = <(String, int)>[];
  int _n = 0;
  _Video(this.name) : dir = Directory('$_dir/_fr_$name')..createSync(recursive: true);

  /// Прошло ~1/12 с (67 мс + 16 мс на кадр снимка) → кадр.
  Future<void> step(WidgetTester t, GlobalKey key, {int hold = 1}) async {
    await t.pump(const Duration(milliseconds: 67));
    final p = '${dir.path}/f${(++_n).toString().padLeft(4, '0')}.png';
    await _shot(t, key, p);
    frames.add((p.split('/').last, hold));
    if (hold > 1) await t.pump(Duration(milliseconds: 83 * (hold - 1)));
  }

  Future<void> steps(WidgetTester t, GlobalKey key, int n) async {
    for (var i = 0; i < n; i++) {
      await step(t, key);
    }
  }

  void finish() {
    final b = StringBuffer();
    for (final f in frames) {
      b.writeln("file '${f.$1}'");
      b.writeln('duration ${(f.$2 / 12).toStringAsFixed(6)}');
    }
    b.writeln("file '${frames.last.$1}'"); // последнему кадру нужна повторная строка (особенность concat)
    File('${dir.path}/list.txt').writeAsStringSync(b.toString());
  }
}

Widget _fakeCamera() => const DecoratedBox(
  decoration: BoxDecoration(
    gradient: RadialGradient(center: Alignment(0, -.2), radius: 1.1, colors: [Color(0xFF2A4256), Color(0xFF14232F), Color(0xFF071824)]),
  ),
  child: SizedBox.expand(),
);

void _muteChannels() {
  TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger.setMockMethodCallHandler(SystemChannels.platform, (c) async => null);
  TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger.setMockMethodCallHandler(const MethodChannel('kg.nurcrm.nurmarket/beep'), (c) async => null);
}

class _Fixed extends SessionController {
  final Session s;
  _Fixed(this.s);
  @override
  Session? build() => s;
}

const _owner = Session(email: 'ainura@nur.kg', access: 'a', refresh: 'r', userId: 'u1', userName: 'Айнура Т.', role: 'owner', isOwner: true, companyName: 'Нур', cashboxId: 'b', cashboxName: 'Касса 1', perms: allPerms);

Product _p(String id, String name, {double price = 100, double qty = 10, String unit = 'шт'}) => Product(
  id: id,
  name: name,
  price: price,
  wholesalePrice: 0,
  promoRules: '',
  minQuantity: 0,
  quantity: qty,
  unit: unit,
  isWeight: false,
  isService: false,
  isFavorite: false,
  altBarcodes: '',
  category: '',
  searchKey: name.toLowerCase(),
  barcode: null,
);

ProductDraft _draft(Product p, {double purchase = 0}) =>
    ProductDraft(name: p.name, unit: p.unit, category: '', barcode: '', alt: const [], price: p.price, purchase: purchase, wholesale: 0, minQty: null);

/// Стенд «цена»: каталог магазина «Нур» в фейковом хозяине действий (настоящие инструменты set_price и «Отменить»).
Rig _priceRig({AiJournal? journal}) {
  final rig = Rig(journal: journal);
  final nan = _p('n1', 'Нан', price: 30, qty: 40);
  final adigene = _p('n2', 'Адыгене 1,5 л', price: 50, qty: 24);
  rig.host.products = [nan, adigene];
  rig.host.drafts['n1'] = _draft(nan, purchase: 22);
  rig.host.drafts['n2'] = _draft(adigene, purchase: 38);
  return rig;
}

// ---------------------------------------------------------------------------------------------------------------- данные для сводки

DateTime get _today => DateUtils.dateOnly(DateTime.now());

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
    ProductSales('Нан', 310, 9300, 2400),
    ProductSales('Адыгене 1,5 л', 130, 7150, 1300),
    ProductSales('Winston Blue', 62, 12710, 1900),
    ProductSales('Сүт', 118, 7670, 1400),
  ];
}

// ---------------------------------------------------------------------------------------------------------------- «Сканер для кассы»

const _key = 'AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8';
final _qr = 'nurmarket-link:v1?d=KASSA-1&h=192.168.1.20&p=47830&k=$_key&n=${Uri.encodeComponent('Нур-1')}&c=1a2b3c4d';

/// Страница «Наведите камеру на QR» (KassaQrScanPage) без плагина камеры: те же виджеты и строки, на месте кадра — тёмный фон.
/// Настоящую страницу собрать нельзя: MobileScanner в тесте без камеры показывает «Камера недоступна».
class _QrPageMock extends StatelessWidget {
  const _QrPageMock();
  @override
  Widget build(BuildContext context) {
    final l = context.l;
    return Scaffold(
      backgroundColor: const Color(0xFF071824),
      body: Stack(
        children: [
          Positioned.fill(child: _fakeCamera()),
          Align(
            alignment: Alignment.topCenter,
            child: SafeArea(
              child: Padding(
                padding: const EdgeInsets.all(14),
                child: Row(
                  children: [
                    Material(
                      color: camShade,
                      shape: const CircleBorder(),
                      child: const SizedBox(width: 48, height: 48, child: Icon(Icons.close_rounded, color: Colors.white)),
                    ),
                    const SizedBox(width: 10),
                    Expanded(child: CamLabel(l.klScanQrTitle, size: 14, weight: FontWeight.w700, maxLines: 3)),
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

// ---------------------------------------------------------------------------------------------------------------- умная камера

/// «По виду»: стадии 0 — наведение, 1 — «Сканирую…», 2 — рамка стянулась, ИИ проверяет, 3 — результат и «Не тот товар».
class _SmartCamMock extends StatelessWidget {
  final ValueNotifier<int> stage;
  const _SmartCamMock(this.stage);

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    return ValueListenableBuilder<int>(
      valueListenable: stage,
      builder: (context, st, _) {
        final size = MediaQuery.sizeOf(context);
        final shrunk = st >= 2;
        final frame = Rect.fromCenter(
          center: Offset(size.width / 2, size.height * (shrunk ? .3 : .42)),
          width: size.width * .84,
          height: size.height * (shrunk ? .3 : .42),
        );
        final busy = st == 1;
        Widget? panel;
        if (st >= 2) {
          panel = Container(
            key: ValueKey(st),
            margin: const EdgeInsets.only(bottom: 12),
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
                    Text(l.scanTook('1,2'), style: TextStyle(color: pal.textSoft, fontSize: 12)),
                    const Spacer(),
                    const Icon(Icons.close_rounded),
                  ],
                ),
                if (st == 2)
                  Padding(
                    padding: const EdgeInsets.only(top: 6, bottom: 6),
                    child: Row(
                      children: [
                        const SizedBox(width: 14, height: 14, child: CircularProgressIndicator(strokeWidth: 2)),
                        const SizedBox(width: 8),
                        Expanded(child: Text(l.scanChecking, style: TextStyle(color: pal.textSoft, fontSize: 12))),
                      ],
                    ),
                  ),
                const SizedBox(height: 8),
                Text('Адыгене 1,5 л · ×1 · ${l.exactMatch}', style: const TextStyle(color: T.success, fontWeight: FontWeight.w700)),
                ListTile(
                  contentPadding: EdgeInsets.zero,
                  dense: true,
                  leading: const ProductThumb(name: 'Адыгене 1,5 л', size: 40),
                  title: const Text('Адыгене 1,5 л', style: TextStyle(fontWeight: FontWeight.w800)),
                  subtitle: Text('${money(55)} ${l.som} · ${l.inStock(qtyText(24), 'шт')}'),
                  trailing: const Icon(Icons.add_shopping_cart_rounded),
                ),
                if (st == 3)
                  TextButton.icon(
                    style: TextButton.styleFrom(minimumSize: const Size(48, 48), padding: EdgeInsets.zero),
                    onPressed: () {},
                    icon: const Icon(Icons.undo_rounded, size: 18),
                    label: Text(l.camNotThis),
                  ),
              ],
            ),
          );
        }
        return Scaffold(
          backgroundColor: Colors.black,
          body: Stack(
            fit: StackFit.expand,
            children: [
              _fakeCamera(),
              AnimatedPositioned.fromRect(
                duration: const Duration(milliseconds: 500),
                curve: Curves.easeInOutCubic,
                rect: frame,
                child: const ScanFrame(),
              ),
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
              AnimatedPositioned(
                duration: const Duration(milliseconds: 500),
                curve: Curves.easeInOutCubic,
                left: 16,
                right: 16,
                top: frame.bottom + 12,
                child: Column(
                  mainAxisSize: MainAxisSize.min,
                  children: [CamLabel(busy ? l.scanBusy : (st >= 2 ? l.scanNext : l.liveHintSale), size: 15, weight: FontWeight.w700, align: TextAlign.center, maxLines: 3)],
                ),
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
                        if (panel != null) panel,
                        FilledButton.icon(
                          style: FilledButton.styleFrom(
                            backgroundColor: T.accent,
                            foregroundColor: T.navy,
                            disabledBackgroundColor: T.accent.withValues(alpha: .55),
                            disabledForegroundColor: T.navy,
                            minimumSize: const Size.fromHeight(64),
                            shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(32)),
                            textStyle: const TextStyle(fontSize: 18, fontWeight: FontWeight.w800),
                          ),
                          onPressed: busy ? null : () {},
                          icon: busy
                              ? const SizedBox(width: 24, height: 24, child: CircularProgressIndicator(strokeWidth: 3, color: T.navy))
                              : const Icon(Icons.center_focus_strong_rounded, size: 28),
                          label: Text(busy ? l.scanBusy : l.scanShoot),
                        ),
                      ],
                    ),
                  ),
                ),
              ),
            ],
          ),
        );
      },
    );
  }
}

// ---------------------------------------------------------------------------------------------------------------- главная кассира

class _OpenShift extends ShiftController {
  @override
  ShiftState build() => ShiftState(
    shift: ShiftInfo(id: 's1', cashboxId: 'b', openedAt: DateTime(2026, 10, 29, 9, 0), openingCash: 3000, expectedCash: 21200, salesTotal: 31450, cashSales: 18200, nonCashSales: 11250, debtSales: 2000, salesCount: 41),
    fetchedAt: DateTime.now(),
  );
  @override
  Future<void> refresh() async {}
}

class _NoNet extends ApiClient {
  _NoNet() : super(SessionStore());
}

// ---------------------------------------------------------------------------------------------------------------- тесты

void main() {
  final shot = GlobalKey();

  setUpAll(() {
    if (_dir.isNotEmpty) Directory(_dir).createSync(recursive: true);
  });

  // ------------------------------------------------------------------------------------------ 1. сопряжение
  testWidgets('реклама: сканер для кассы — сопряжение (01-mobile-pair)', (t) async {
    await t.runAsync(_fonts);
    _phone(t);
    _muteChannels();
    final hello = Completer<void>();
    final link = KassaLink(
      store: MemoryKassaLinkStore(),
      subnets: () async => <String>[],
      retryEvery: const Duration(milliseconds: 20),
      transport: (host, port, line, c, r) async {
        await hello.future;
        return jsonEncode({'ok': true, 'code': 'ok', 'kassa': 'Нур-1', 'company': '1a2b3c4d', 'shift_open': true});
      },
    );
    await t.runAsync(link.ensureLoaded);
    final nav = GlobalKey<NavigatorState>();
    await t.pumpWidget(
      RepaintBoundary(
        key: shot,
        child: _app('ru', KassaLinkScreen(link: link), nav: nav, overrides: [sessionProvider.overrideWith(() => _Fixed(_owner))]),
      ),
    );
    await t.pump(const Duration(milliseconds: 100));
    final v = _Video('01-mobile-pair');
    // 1) экран «Связь с кассой»: касса не подключена, «Сканировать QR кассы»
    await v.step(t, shot, hold: 14);
    // 2) открылся сканер QR: камера направлена на экран кассы
    unawaited(nav.currentState!.push(MaterialPageRoute<void>(builder: (_) => const _QrPageMock())));
    await v.steps(t, shot, 5);
    await v.step(t, shot, hold: 20);
    // 3) QR прочитан → страница закрылась, идёт проверка связи
    final pairing = link.pairFromQr(_qr);
    nav.currentState!.pop();
    await v.steps(t, shot, 5);
    await v.step(t, shot, hold: 6);
    hello.complete();
    await pairing;
    await t.pump(const Duration(milliseconds: 40));
    // 4) подключено: «На связи», касса «Нур-1», плашка «Касса «Нур-1» подключена»
    toast(t.element(find.byType(KassaLinkScreen)), AppLocalizations.of(t.element(find.byType(KassaLinkScreen)))!.klConnected('Нур-1'));
    await v.steps(t, shot, 6);
    await v.step(t, shot, hold: 24);
    expect(link.status, KassaLinkStatus.online);
    expect(t.takeException(), isNull);
    v.finish();
    await t.pumpWidget(const SizedBox());
    link.dispose();
  }, skip: _dir.isEmpty);

  // ------------------------------------------------------------------------------------------ 2. сканирование
  testWidgets('реклама: сканер для кассы — скан товара (01-mobile-scan)', (t) async {
    await t.runAsync(_fonts);
    _phone(t);
    _muteChannels();
    final reply = Completer<void>();
    final link = KassaLink(
      store: MemoryKassaLinkStore(),
      subnets: () async => <String>[],
      retryEvery: const Duration(milliseconds: 20),
      transport: (host, port, line, c, r) async {
        final req = jsonDecode(line) as Map<String, dynamic>;
        if (req['op'] == 'hello') return jsonEncode({'ok': true, 'code': 'ok', 'kassa': 'Нур-1', 'company': '1a2b3c4d', 'shift_open': true});
        await reply.future;
        return jsonEncode({'ok': true, 'code': 'added', 'name': 'Нан'});
      },
    );
    final frameKey = GlobalKey<ScanFrameState>();
    final ctl = KassaScannerController(link: link, onFlash: (ok) => ok ? frameKey.currentState?.success() : frameKey.currentState?.fail());
    await t.runAsync(() async {
      await link.ensureLoaded();
      await link.pairFromQr(_qr);
      await ctl.start();
    });
    await t.pumpWidget(
      RepaintBoundary(
        key: shot,
        child: _app(
          'ru',
          Scaffold(
            backgroundColor: const Color(0xFF071824),
            body: Stack(
              fit: StackFit.expand,
              children: [
                _fakeCamera(),
                Align(alignment: Alignment.center, child: ScanFrame(key: frameKey)),
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
    await t.pump(const Duration(milliseconds: 100));
    final v = _Video('01-mobile-scan');
    // наведение: бегущий луч, панель «Наведите на штрихкод…»
    await v.steps(t, shot, 18);
    // код увиден → «Отправляю…»
    ctl.onCode('4870001234567');
    await v.steps(t, shot, 4);
    // касса ответила «добавлено»: рамка вспыхивает зелёным (пик), строка «Нан · код»
    reply.complete();
    await t.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 60)));
    await t.pump(const Duration(milliseconds: 10));
    await v.steps(t, shot, 10);
    await v.steps(t, shot, 22);
    expect(ctl.lastStatus, KassaScanStatus.added);
    expect(ctl.lastProduct, 'Нан');
    expect(t.takeException(), isNull);
    v.finish();
    await t.pumpWidget(const SizedBox());
    ctl.dispose();
    link.dispose();
  }, skip: _dir.isEmpty);

  // ------------------------------------------------------------------------------------------ 3. видеозвонок Нур Советника
  testWidgets('реклама: Нур Советник — голосовая смена цены (02-mobile-advisor-call)', (t) async {
    await t.runAsync(_fonts);
    _phone(t);
    final dir = Directory.systemTemp.createTempSync('nm_ad_call2_');
    Audit.testDirectory = dir;
    AiJournal.testDirectory = dir;
    addTearDown(() {
      try {
        dir.deleteSync(recursive: true);
      } catch (_) {}
    });
    // настоящий инструмент set_price на фейковом каталоге: берём его итоговую фразу
    final real = _priceRig();
    real.host.products = [for (final p in real.host.products) p];
    late final ToolResult res;
    await t.runAsync(() async {
      res = await real.say('Поменяй цену на хлеб на 28', 'set_price', {'query': 'Нан', 'price': 28}, _owner);
    });
    expect(res.status, ToolStatus.ok);
    expect(real.host.drafts['n1']!.price, 28);
    final say = res.say;

    final rig = CallRig(lang: 'ru', session: _owner);
    await t.pumpWidget(
      RepaintBoundary(
        key: shot,
        child: UncontrolledProviderScope(
          container: rig.container,
          child: MaterialApp(
            debugShowCheckedModeBanner: false,
            locale: const Locale('ru'),
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
    rig.c.registry!.register(
      AssistantTool(
        decl: const ToolDecl('set_price_ad', 'ad'),
        group: ToolGroup.action,
        intentWords: Intents.price,
        run: (r) async => ToolResult(ToolStatus.ok, say, undo: true),
      ),
    );
    final v = _Video('02-mobile-advisor-call');
    await v.steps(t, shot, 10);
    // голос: фраза приходит кусками, как расшифровка Gemini
    rig.socket.said('Поменяй');
    await v.steps(t, shot, 3);
    rig.socket.said(' цену');
    await v.steps(t, shot, 3);
    rig.socket.said(' на хлеб');
    await v.steps(t, shot, 3);
    rig.socket.said(' на 28');
    await v.steps(t, shot, 5);
    // ИИ вызвал инструмент: сверху карточка «ИИ: … цена 28 (было 30) · Отменить»
    rig.socket.toolCall('ad1', 'set_price_ad');
    for (var i = 0; i < 4; i++) {
      await rig.settle(t, 2);
      await v.step(t, shot);
    }
    rig.socket.aiSaid('Готово, цена на Нан теперь 28 сом.');
    rig.socket.push({
      'serverContent': {
        'modelTurn': {
          'parts': [
            {
              'inlineData': {'mimeType': 'audio/pcm;rate=24000', 'data': base64Encode(List.filled(96000, 0))},
            },
          ],
        },
      },
    });
    await v.steps(t, shot, 30);
    expect(t.takeException(), isNull);
    expect(rig.socket.responseOf('ad1')?['status'], 'ok');
    expect(find.text(lookupAppLocalizations(const Locale('ru')).aiUndo), findsOneWidget);
    v.finish();
    await rig.dispose(t);
    await t.pumpWidget(const SizedBox());
  }, skip: _dir.isEmpty);

  // ------------------------------------------------------------------------------------------ 4. «Действия ИИ» после «Отменить»
  testWidgets('реклама: Действия ИИ после «Отменить» (02-mobile-ai-undo)', (t) async {
    await t.runAsync(_fonts);
    _phone(t);
    final dir = Directory.systemTemp.createTempSync('nm_ad_log_');
    Audit.testDirectory = dir;
    AiJournal.testDirectory = dir;
    addTearDown(() {
      try {
        dir.deleteSync(recursive: true);
      } catch (_) {}
    });
    final rig = _priceRig(journal: AiJournal());
    await t.runAsync(() async {
      AiJournal.resetQueueForTests(); // очередь записи — в настоящем времени, не в «поддельных» часах теста
      Audit.resetForTests();
      rig.now = DateTime.now().subtract(const Duration(minutes: 9));
      await rig.say('Поставь Адыгене 1,5 литра по 55', 'set_price', {'query': 'Адыгене', 'price': 55}, _owner);
      rig.now = DateTime.now().subtract(const Duration(minutes: 2));
      await rig.say('Поменяй цену на хлеб на 28', 'set_price', {'query': 'Нан', 'price': 28}, _owner);
    });
    expect(rig.host.drafts['n1']!.price, 28);
    final db = AppDatabase(NativeDatabase.memory());
    final router = GoRouter(routes: [GoRoute(path: '/', builder: (_, _) => const AiLogScreen())]);
    await t.pumpWidget(
      RepaintBoundary(
        key: shot,
        child: ProviderScope(
          key: UniqueKey(),
          overrides: [
            databaseProvider.overrideWithValue(db),
            sessionProvider.overrideWith(() => _Fixed(_owner)),
            aiPolicyProvider.overrideWith((ref) async => const AiAccessPolicy.unset()),
            journalUndoerProvider.overrideWithValue(JournalUndoer(rig.host)),
          ],
          child: MaterialApp.router(
            debugShowCheckedModeBanner: false,
            routerConfig: router,
            theme: buildTheme(dark: false),
            locale: const Locale('ru'),
            supportedLocales: AppLocalizations.supportedLocales,
            localizationsDelegates: _delegates,
          ),
        ),
      ),
    );
    Future<void> settle({bool Function()? until}) async {
      for (var i = 0; i < 200; i++) {
        await t.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 50)));
        await t.pump(const Duration(milliseconds: 50));
        final loading = find.byType(CircularProgressIndicator).evaluate().isNotEmpty;
        if (i >= 2 && !loading && (until == null || until())) break;
      }
      await t.pump(const Duration(milliseconds: 300));
    }

    await settle();
    final l = lookupAppLocalizations(const Locale('ru'));
    expect(find.text(l.aiUndo), findsNWidgets(2));
    await t.tap(find.text(l.aiUndo).first); // верхняя запись — Нан
    await settle(until: () => rig.host.drafts['n1']!.price == 30);
    await t.pump(const Duration(milliseconds: 500));
    expect(rig.host.drafts['n1']!.price, 30);
    expect(t.takeException(), isNull);
    await _save(t, shot, '02-mobile-ai-undo');
    await t.pumpWidget(const SizedBox());
    await t.pump(const Duration(seconds: 5)); // снять таймеры «тоста»
  }, skip: _dir.isEmpty);

  // ------------------------------------------------------------------------------------------ 5. умная камера
  testWidgets('реклама: умная камера «По виду» (03-mobile-smart-camera)', (t) async {
    await t.runAsync(_fonts);
    _phone(t);
    final stage = ValueNotifier<int>(0);
    await t.pumpWidget(RepaintBoundary(key: shot, child: _app('ru', _SmartCamMock(stage))));
    await t.pump(const Duration(milliseconds: 100));
    final v = _Video('03-mobile-smart-camera');
    await v.steps(t, shot, 14); // наведение: рамка крупная
    stage.value = 1; // нажали «Сканировать»
    await v.steps(t, shot, 8);
    stage.value = 2; // рамка стягивается, внизу — «На снимке», ИИ проверяет
    await v.steps(t, shot, 9);
    stage.value = 3; // результат и «Не тот товар»
    await v.steps(t, shot, 6);
    await v.steps(t, shot, 22);
    expect(t.takeException(), isNull);
    v.finish();
    await t.pumpWidget(const SizedBox());
  }, skip: _dir.isEmpty);

  // ------------------------------------------------------------------------------------------ 6. главная на кыргызском
  testWidgets('реклама: главная на кыргызском (10-mobile-lang-ky)', (t) async {
    await t.runAsync(_fonts);
    _phone(t);
    final db = AppDatabase(NativeDatabase.memory());
    await t.runAsync(() async {
      await db.replaceProducts([
        ProductsCompanion.insert(id: 'a', name: 'Нан', price: const Value(30), quantity: const Value(4), searchKey: const Value('нан')),
        ProductsCompanion.insert(id: 'b', name: 'Сүт', price: const Value(65), quantity: const Value(6), searchKey: const Value('сүт')),
        ProductsCompanion.insert(id: 'c', name: 'Адыгене 1,5 л', price: const Value(55), quantity: const Value(3), searchKey: const Value('адыгене')),
        ProductsCompanion.insert(id: 'd', name: 'Winston Blue', price: const Value(205), quantity: const Value(8), searchKey: const Value('winston')),
        ProductsCompanion.insert(id: 'e', name: 'Кант', price: const Value(78), quantity: const Value(60), searchKey: const Value('кант')),
      ]);
    });
    await t.pumpWidget(
      RepaintBoundary(
        key: shot,
        child: _app(
          'ky',
          const Scaffold(body: HomeScreen()),
          overrides: [
            databaseProvider.overrideWithValue(db),
            pendingCountProvider.overrideWith((ref) => Stream.value(0)),
            apiProvider.overrideWithValue(_NoNet()),
            sessionProvider.overrideWith(() => _Fixed(_owner)),
            shiftProvider.overrideWith(() => _OpenShift()),
            myTasksProvider.overrideWith((ref) async => []),
          ],
        ),
      ),
    );
    for (var i = 0; i < 8; i++) {
      await t.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 60)));
      await t.pump(const Duration(milliseconds: 100));
    }
    expect(t.takeException(), isNull);
    await _save(t, shot, '10-mobile-lang-ky');
    await t.pumpWidget(const SizedBox());
  }, skip: _dir.isEmpty);

  // ------------------------------------------------------------------------------------------ 7. главная со сводкой
  testWidgets('реклама: главная со сводкой (13-mobile-home)', (t) async {
    await t.runAsync(_fonts);
    _phone(t);
    await t.pumpWidget(RepaintBoundary(key: shot, child: _app('ru', const SummaryScreen(), overrides: [analyticsRepoProvider.overrideWithValue(_Repo())])));
    await t.pump(const Duration(milliseconds: 300));
    await t.pump(const Duration(milliseconds: 300));
    final l = lookupAppLocalizations(const Locale('ru'));
    final cmp = find.text(l.infCompare);
    if (cmp.evaluate().isNotEmpty) {
      await t.tap(cmp.first);
      await t.pump(const Duration(milliseconds: 300));
      await t.pump(const Duration(milliseconds: 600));
    }
    expect(t.takeException(), isNull);
    await _save(t, shot, '13-mobile-home');
    await t.pumpWidget(const SizedBox());
  }, skip: _dir.isEmpty);
}
