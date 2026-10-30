// 2026-10-30 (рекламные ролики, «фишки», вторая партия): кадры мобилки на телефоне 360x780 dp, плотность 3 (1080x2340 px), светлая
// тема, язык ru. Образец и хелперы — ad_features_snapshots_test.dart (скопированы, тот файл не тронут). Данные выдуманные
// (магазин «Нур»). Сеть, камера, микрофон, Gemini и боевая база не трогаются: сокет, звук, каталог, сервер — подставные.
// Без --dart-define=SNAP_DIR=<папка> тест пропускается, в репозиторий ничего не пишется.
// Запуск: flutter test test/ad_details_snapshots_test.dart --dart-define=SNAP_DIR=<папка> [--plain-name "18-mobile-cam-prop"]
// PNG-снимки кладутся в <папка>; для видео — кадры в <папка>/_fr_<имя>/ + list.txt (12 к/с, склеивает ffmpeg вне теста).
import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:math' as math;
import 'dart:typed_data';
import 'dart:ui' as ui;

import 'package:drift/drift.dart' as drift show GeneratedDatabase, Table, TableInfo, Value;
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
import 'package:nurmarket/core/ai/assistant/call_table.dart';
import 'package:nurmarket/core/ai/assistant/intent_gate.dart';
import 'package:nurmarket/core/ai/assistant/journal_undo.dart';
import 'package:nurmarket/core/ai/assistant/product_wizard.dart' show WizardBackend, WizardDraft;
import 'package:nurmarket/core/ai/assistant/tool_registry.dart';
import 'package:nurmarket/core/ai/cam_aliases.dart';
import 'package:nurmarket/core/ai/live_tools.dart';
import 'package:nurmarket/core/ai/vision.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/catalog/global_products.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/invoice/invoice_match.dart' show InvoiceGuard, SupplierRef;
import 'package:nurmarket/core/invoice/invoice_parser.dart';
import 'package:nurmarket/core/providers.dart';
import 'package:nurmarket/core/theme/theme.dart';
import 'package:nurmarket/core/theme/tokens.dart';
import 'package:nurmarket/core/util/audit.dart';
import 'package:nurmarket/core/util/ui.dart';
import 'package:nurmarket/features/admin/invoice_import.dart';
import 'package:nurmarket/features/assistant/ai_log_screen.dart';
import 'package:nurmarket/features/assistant/call_consent.dart';
import 'package:nurmarket/features/assistant/call_overlay.dart';
import 'package:nurmarket/features/scan/cam_label.dart';
import 'package:nurmarket/features/scan/scan_animations.dart';
import 'package:nurmarket/features/scan/smart_camera.dart';
import 'package:nurmarket/features/stock/product_edit_repository.dart' show ProductDraft;
import 'package:nurmarket/l10n/app_localizations.dart';

import 'action_tools_test.dart' show Rig;
import 'admin_stock_fixes_test.dart' show FakeApi;
import 'support/call_rig.dart';

const _dir = String.fromEnvironment('SNAP_DIR');

/// Плотность текущего снимка: 3 (телефон 360x780 dp); для длинных экранов ниже (больше dp на тех же 1080x2340 px).
double _pr = 3;

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

void _phone(WidgetTester t, {double dpr = 3}) {
  _pr = dpr;
  t.view.physicalSize = const Size(1080, 2340);
  t.view.devicePixelRatio = dpr;
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
    _pr = 3;
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
  // кадр, правка шрифта, ещё кадр (текст, перестроенный в этом кадре, снова без шрифта) и снова правка: иначе в кадре бывают «квадраты»
  _fixDefaultFont(RendererBinding.instance.renderView);
  await t.pump(const Duration(milliseconds: 16));
  _fixDefaultFont(RendererBinding.instance.renderView);
  await t.pump();
  _fixDefaultFont(RendererBinding.instance.renderView);
  await t.pump();
  await t.runAsync(() async {
    final b = key.currentContext!.findRenderObject()! as RenderRepaintBoundary;
    final img = await b.toImage(pixelRatio: _pr);
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
  _Video(this.name) : dir = Directory('$_dir/_fr_$name')..createSync(recursive: true) {
    for (final f in dir.listSync()) {
      if (f is File) f.deleteSync(); // прежний прогон не должен подмешиваться
    }
  }

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

class _Fixed extends SessionController {
  final Session s;
  _Fixed(this.s);
  @override
  Session? build() => s;
}

const _owner = Session(email: 'ainura@nur.kg', access: 'a', refresh: 'r', userId: 'u1', userName: 'Айнура Т.', role: 'owner', isOwner: true, companyName: 'Нур', cashboxId: 'b', cashboxName: 'Касса 1', perms: allPerms);

Product _p(String id, String name, {double price = 100, double qty = 10, String unit = 'шт', String? barcode}) => Product(
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
  barcode: barcode,
);

ProductDraft _draft(Product p, {double purchase = 0}) =>
    ProductDraft(name: p.name, unit: p.unit, category: '', barcode: p.barcode ?? '', alt: const [], price: p.price, purchase: purchase, wholesale: 0, minQty: null);

/// Реальное время дня в записях журнала (чтобы «сегодня» и «ЧЧ:ММ» были похожи на правду).
Future<void> _realDelay(WidgetTester t, [int ms = 40]) => t.runAsync(() => Future<void>.delayed(Duration(milliseconds: ms)));

// ---------------------------------------------------------------------------------------------------------------- A. умная камера с реквизитом

/// Реквизит вместо снимка камеры: нарисованная пластиковая бутыль 1,5 л (голубой корпус, белая этикетка «АДЫГЕНЕ 1,5 л»).
/// Бутыль стоит так, чтобы лежать внутри рамки в ОБЕИХ её позициях (крупной и стянутой): рамка 0,21…0,63 высоты → 0,15…0,45.
class _BottlePainter extends CustomPainter {
  const _BottlePainter();

  @override
  void paint(Canvas canvas, Size size) {
    final w = size.width, h = size.height;
    final cx = w / 2;
    final bw = w * .52; // половина ширины корпуса = bw/2
    final half = bw / 2;
    final neck = half * .34;
    final shoulderY = h * .30;
    final body = Path()
      ..moveTo(cx - neck, h * .075)
      ..lineTo(cx - neck, h * .155)
      ..cubicTo(cx - neck, h * .22, cx - half, h * .21, cx - half, shoulderY + h * .05)
      ..lineTo(cx - half, h - 12)
      ..quadraticBezierTo(cx - half, h, cx - half + 14, h)
      ..lineTo(cx + half - 14, h)
      ..quadraticBezierTo(cx + half, h, cx + half, h - 12)
      ..lineTo(cx + half, shoulderY + h * .05)
      ..cubicTo(cx + half, h * .21, cx + neck, h * .22, cx + neck, h * .155)
      ..lineTo(cx + neck, h * .075)
      ..close();
    // мягкая тень под бутылью
    canvas.drawOval(Rect.fromCenter(center: Offset(cx, h + 2), width: bw * 1.5, height: 10), Paint()..color = const Color(0x66000000)..maskFilter = const MaskFilter.blur(BlurStyle.normal, 5));
    // корпус: голубая вода с вертикальным градиентом
    canvas.drawPath(
      body,
      Paint()..shader = const LinearGradient(begin: Alignment.centerLeft, end: Alignment.centerRight, colors: [Color(0xFF2B86C9), Color(0xFF6CC4F0), Color(0xFF3A97DA), Color(0xFF1F6FAE)], stops: [0, .35, .7, 1]).createShader(Rect.fromLTWH(cx - half, 0, bw, h)),
    );
    canvas.save();
    canvas.clipPath(body);
    // кольцевые рёбра пластика на плечах и у дна
    final rib = Paint()..color = const Color(0x33FFFFFF)..strokeWidth = 1.2;
    for (final y in [h * .245, h * .27, h * .295]) {
      canvas.drawLine(Offset(cx - half, y), Offset(cx + half, y), rib);
    }
    for (final y in [h * .90, h * .93, h * .96]) {
      canvas.drawLine(Offset(cx - half, y), Offset(cx + half, y), rib);
    }
    // белая этикетка
    final label = Rect.fromLTWH(cx - half - 1, h * .43, bw + 2, h * .36);
    canvas.drawRect(label, Paint()..color = const Color(0xFFF7FAFC));
    canvas.drawRect(Rect.fromLTWH(cx - half - 1, h * .43, bw + 2, 4), Paint()..color = const Color(0xFF1F6FAE));
    canvas.drawRect(Rect.fromLTWH(cx - half - 1, label.bottom - 4, bw + 2, 4), Paint()..color = const Color(0xFF1F6FAE));
    // волна на этикетке
    final wave = Path()..moveTo(label.left, label.top + 18);
    for (var i = 0; i <= 8; i++) {
      wave.lineTo(label.left + bw * i / 8, label.top + 17 + (i.isEven ? 3 : -3));
    }
    canvas.drawPath(wave, Paint()..color = const Color(0xFF6CC4F0)..style = PaintingStyle.stroke..strokeWidth = 2);
    void text(String s, double top, double size, Color color, FontWeight weight) {
      final tp = TextPainter(
        text: TextSpan(text: s, style: TextStyle(fontFamily: 'Montserrat', fontSize: size, fontWeight: weight, color: color, letterSpacing: .2)),
        textDirection: TextDirection.ltr,
      )..layout(maxWidth: bw);
      tp.paint(canvas, Offset(cx - tp.width / 2, top));
    }

    text('АДЫГЕНЕ', label.top + 22, 8.2, const Color(0xFF1F6FAE), FontWeight.w800);
    text('1,5 л', label.top + 35, 11, const Color(0xFF0D4C86), FontWeight.w800);
    // блик слева
    canvas.drawRect(Rect.fromLTWH(cx - half + 7, h * .2, 5, h * .75), Paint()..color = const Color(0x55FFFFFF));
    canvas.restore();
    canvas.drawPath(body, Paint()..style = PaintingStyle.stroke..strokeWidth = 1.2..color = const Color(0x55FFFFFF));
    // крышка
    final cap = RRect.fromRectAndRadius(Rect.fromLTWH(cx - neck - 2, 0, neck * 2 + 4, h * .085), const Radius.circular(3));
    canvas.drawRRect(cap, Paint()..color = const Color(0xFF1E5FA0));
    for (var i = 1; i < 6; i++) {
      final x = cx - neck - 2 + (neck * 2 + 4) * i / 6;
      canvas.drawLine(Offset(x, 2), Offset(x, h * .085 - 2), Paint()..color = const Color(0x44FFFFFF)..strokeWidth = 1);
    }
    canvas.drawRect(Rect.fromLTWH(cx - neck - 1, h * .085, neck * 2 + 2, 3), Paint()..color = const Color(0xCCFFFFFF)); // кольцо горлышка
  }

  @override
  bool shouldRepaint(covariant CustomPainter old) => false;
}

/// Тёмная сцена: приглушённый свет, полка, бутыль по центру, мелкая подпись «пример (иллюстрация)».
Widget _propCamera() => LayoutBuilder(
  builder: (context, c) {
    final w = c.maxWidth, h = c.maxHeight;
    final top = h * .222; // 173 dp при высоте 780
    final bottleH = h * .222; // до 0,444 высоты: ниже низа стянутой рамки (0,45)
    return Stack(
      fit: StackFit.expand,
      children: [
        const DecoratedBox(
          decoration: BoxDecoration(gradient: RadialGradient(center: Alignment(0, -.3), radius: 1.05, colors: [Color(0xFF2E4A5F), Color(0xFF15262F), Color(0xFF071824)])),
        ),
        // полка: чуть светлее, чем фон, с кромкой под бутылью
        Positioned(
          left: 0,
          right: 0,
          top: top + bottleH,
          bottom: 0,
          child: const DecoratedBox(
            decoration: BoxDecoration(
              gradient: LinearGradient(begin: Alignment.topCenter, end: Alignment.bottomCenter, colors: [Color(0xFF26394A), Color(0xFF0D1B26)]),
              border: Border(top: BorderSide(color: Color(0x33FFFFFF), width: 1)),
            ),
          ),
        ),
        // размытые огоньки на заднем плане
        for (final (dx, dy, r) in [(.18, .22, 26.0), (.82, .27, 20.0), (.7, .17, 14.0), (.3, .3, 12.0)])
          Positioned(left: w * dx - r, top: h * dy - r, child: Container(width: r * 2, height: r * 2, decoration: const BoxDecoration(shape: BoxShape.circle, color: Color(0x0DFFFFFF)))),
        Positioned(left: (w - bottleH * .66) / 2, top: top, width: bottleH * .66, height: bottleH, child: const CustomPaint(painter: _BottlePainter())),
        const Positioned(
          left: 0,
          right: 0,
          bottom: 3,
          child: Center(child: Text('пример (иллюстрация)', style: TextStyle(color: Color(0x99FFFFFF), fontSize: 10, fontFamily: 'Montserrat'))),
        ),
      ],
    );
  },
);

/// Копия `_SmartCamMock` из ad_features_snapshots_test: «По виду» — наведение, «Сканирую…», рамка стянулась, результат. Фон — реквизит.
class _SmartCamProp extends StatelessWidget {
  final ValueNotifier<int> stage;
  const _SmartCamProp(this.stage);

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
              _propCamera(),
              AnimatedPositioned.fromRect(duration: const Duration(milliseconds: 500), curve: Curves.easeInOutCubic, rect: frame, child: const ScanFrame()),
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

// ---------------------------------------------------------------------------------------------------------------- B. настоящая умная камера

class _Vision extends VisionService {
  final List<SeenItem> out;
  _Vision(super.api, super.db, this.out);
  @override
  Future<String?> key() async => 'k';
  @override
  Future<List<SeenItem>> recognize(Uint8List jpeg, {String mode = 'sale', String mime = 'image/jpeg'}) async => out;
}

// как в smart_camera_pick_test: владелец без userId — окно «ИИ — общий лимит» не показывается
const _camOwner = Session(email: 'a', access: '', refresh: '', role: 'owner', isOwner: true, perms: []);

class _CamStand {
  final AppDatabase db = AppDatabase(NativeDatabase.memory());
  late final FakeApi api;
  final shared = <String, dynamic>{};
  final GlobalKey shot = GlobalKey();

  Future<void> fill(WidgetTester t) async {
    CamAliases.resetForTest();
    api = FakeApi((m, p, b, q) async {
      if (p == 'api/main/app-settings/nurmarket-mobile/') {
        if (m == 'PATCH') shared.addAll((b as Map).cast<String, dynamic>());
        return {'settings': shared};
      }
      if (m == 'GET' && p == 'api/main/products/list/') return {'results': []};
      throw ApiException('не ожидали $m $p', status: 404);
    })..attach(_camOwner);
    await t.runAsync(() async {
      Future<void> put(String id, String name, String code) => db.upsertProduct(
        ProductsCompanion.insert(id: id, name: name, barcode: drift.Value(code.isEmpty ? null : code), quantity: const drift.Value(24), price: const drift.Value(55), searchKey: drift.Value('${name.toLowerCase()} $code')),
      );
      await put('p1', 'Адыгене 1л', '4700000310326');
      await put('p2', 'ASU Негаз 1 л клуб-киви', '4870001571039');
      await put('p3', 'ASU Негаз 1л персик', '4870001571528');
      await put('p4', 'ASU Негаз 0,5 л персик', '4870001571535');
    });
  }

  Future<void> pump(
    WidgetTester t, {
    String? mode,
    String? initialMode,
    List<SeenMatch>? matches,
    Future<List<CamPicked>> Function()? images,
    List<SeenItem> ai = const [],
  }) async {
    final vision = _Vision(api, db, ai);
    final router = GoRouter(routes: [GoRoute(path: '/', builder: (_, _) => SmartCameraScreen(debugMode: mode, initialMode: initialMode, debugMatches: matches, debugPickImages: images))]);
    await t.pumpWidget(
      RepaintBoundary(
        key: shot,
        child: ProviderScope(
          overrides: [
            databaseProvider.overrideWithValue(db),
            apiProvider.overrideWithValue(api),
            visionProvider.overrideWithValue(vision),
            sessionProvider.overrideWith(() => _Fixed(_camOwner)),
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
    await t.pump();
  }

  Future<void> settle(WidgetTester t, [int n = 25]) async {
    for (var i = 0; i < n; i++) {
      await _realDelay(t);
      await t.pump();
    }
  }
}

// ---------------------------------------------------------------------------------------------------------------- C. видеозвонок

/// Нужная для звонка разметка: приложение внутри слоя звонка (как в ad_features_snapshots_test, сцена 02).
Widget _callApp(CallRig rig) => UncontrolledProviderScope(
  container: rig.container,
  child: MaterialApp(
    debugShowCheckedModeBanner: false,
    locale: const Locale('ru'),
    supportedLocales: AppLocalizations.supportedLocales,
    localizationsDelegates: callDelegates,
    theme: buildTheme(dark: false),
    navigatorKey: rig.navKey,
    builder: (context, child) => AssistantOverlay(navigatorKey: rig.navKey, child: child!),
    home: const StubScreen('Склад'),
  ),
);

/// Подставной «справочник NurCRM» и «создание»: без сети. Название по штрихкоду — «Aziz Tea Black Tea 100 г».
class _WizBackend implements WizardBackend {
  final created = <WizardDraft>[];
  @override
  Future<({String name, String? category, String? brand})?> globalInfo(String code) async => (name: 'Aziz Tea Black Tea 100 г', category: null, brand: null);
  @override
  Future<String?> nameFromPhoto(Uint8List jpeg) async => null;
  @override
  Future<String> create(WizardDraft d) async {
    created.add(d);
    return 'new1';
  }

  @override
  Future<int> teach(Product p, List<Uint8List> shots) async => shots.length;
}

/// Кадр «с камеры» для мини-снимков мастера: нарисованная коробка чая (0 спереди, 1 сзади, 2 сбоку, 3 штрихкод). PNG вместо JPEG:
/// подставной кадр должен декодироваться, иначе в слоте останется «красный крест». Это иллюстрация, не снимок настоящей камеры.
Future<Uint8List> _stillPng(int kind) async {
  const w = 240.0, h = 320.0;
  final rec = ui.PictureRecorder();
  final c = Canvas(rec, const Rect.fromLTWH(0, 0, w, h));
  c.drawRect(const Rect.fromLTWH(0, 0, w, h), Paint()..shader = const LinearGradient(begin: Alignment.topCenter, end: Alignment.bottomCenter, colors: [Color(0xFF4A3F36), Color(0xFF2B241F)]).createShader(const Rect.fromLTWH(0, 0, w, h)));
  void text(String s, Offset at, double size, Color color, {FontWeight weight = FontWeight.w800, double maxW = 200}) {
    final tp = TextPainter(text: TextSpan(text: s, style: TextStyle(fontFamily: 'Montserrat', fontSize: size, fontWeight: weight, color: color)), textDirection: TextDirection.ltr, textAlign: TextAlign.center)..layout(maxWidth: maxW);
    tp.paint(c, Offset(at.dx - tp.width / 2, at.dy));
  }

  if (kind == 3) {
    c.drawRRect(RRect.fromRectAndRadius(const Rect.fromLTWH(30, 90, 180, 130), const Radius.circular(6)), Paint()..color = Colors.white);
    final r = math.Random(7);
    var x = 48.0;
    while (x < 190) {
      final bw = 1.0 + r.nextInt(3);
      c.drawRect(Rect.fromLTWH(x, 104, bw, 80), Paint()..color = const Color(0xFF111111));
      x += bw + 1.0 + r.nextInt(3);
    }
    text('4 223949 757774', const Offset(w / 2, 192), 13, const Color(0xFF111111), weight: FontWeight.w600);
  } else {
    final bw = kind == 2 ? 70.0 : 150.0;
    final box = RRect.fromRectAndRadius(Rect.fromCenter(center: const Offset(w / 2, h / 2 + 6), width: bw, height: 220), const Radius.circular(8));
    c.drawRRect(box, Paint()..color = kind == 1 ? const Color(0xFF1F5B3A) : const Color(0xFF16703E));
    c.drawRect(Rect.fromCenter(center: const Offset(w / 2, h / 2 - 58), width: bw, height: 8), Paint()..color = const Color(0xFFD9A441));
    if (kind == 0) {
      text('AZIZ', const Offset(w / 2, 118), 26, const Color(0xFFF2C75C));
      text('Black Tea', const Offset(w / 2, 152), 15, Colors.white, weight: FontWeight.w700);
      c.drawCircle(const Offset(w / 2, 212), 24, Paint()..color = const Color(0xFF8B2E1E));
      text('100 г', const Offset(w / 2, 280), 12, Colors.white, weight: FontWeight.w700);
    } else if (kind == 1) {
      for (var i = 0; i < 7; i++) {
        c.drawRect(Rect.fromLTWH(w / 2 - 55, 122 + i * 18.0, i.isEven ? 110 : 84, 5), Paint()..color = const Color(0x99FFFFFF));
      }
    } else {
      text('AZIZ', Offset(w / 2, 150), 11, const Color(0xFFF2C75C), weight: FontWeight.w800);
    }
  }
  final img = await rec.endRecording().toImage(w.toInt(), h.toInt());
  return (await img.toByteData(format: ui.ImageByteFormat.png))!.buffer.asUint8List();
}

/// Каталог магазина «Нур» в фейковом хозяине действий (настоящие инструменты set_price, product_edit, delete_product).
Rig _shopRig({AiJournal? journal}) {
  final rig = Rig(journal: journal);
  final nan = _p('n1', 'Нан', price: 30, qty: 40);
  final adigene = _p('n2', 'Адыгене 1,5 л', price: 50, qty: 24);
  final seeds = _p('n3', 'Семечки', price: 120, qty: 15, barcode: '4870001234567');
  rig.host.products = [nan, adigene, seeds];
  rig.host.drafts['n1'] = _draft(nan, purchase: 22);
  rig.host.drafts['n2'] = _draft(adigene, purchase: 38);
  rig.host.drafts['n3'] = _draft(seeds, purchase: 90);
  return rig;
}

// ---------------------------------------------------------------------------------------------------------------- D. накладная

class _InvApi extends ApiClient {
  _InvApi() : super(SessionStore());
  @override
  Future<dynamic> get(String path, {Map<String, dynamic>? query, Duration? timeout}) async => {'purchase_price': '38'};
}

class _CatSeed extends drift.GeneratedDatabase {
  _CatSeed(super.e);
  @override
  Iterable<drift.TableInfo<drift.Table, dynamic>> get allTables => const [];
  @override
  int get schemaVersion => 1;
}

/// Мини-справочник NurCRM (как встроенный файл: products + meta) во временной папке.
Future<GlobalProducts> _catalog(Directory dir) async {
  final f = File('${dir.path}/global.sqlite');
  final db = _CatSeed(NativeDatabase(f));
  await db.customStatement('CREATE TABLE products(barcode TEXT PRIMARY KEY, name TEXT NOT NULL, brand TEXT NOT NULL DEFAULT \'\', category TEXT NOT NULL DEFAULT \'\') WITHOUT ROWID');
  await db.customStatement('CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT)');
  await db.customStatement("INSERT INTO meta VALUES('exported_at', '2026-10-26')");
  await db.customStatement('INSERT INTO products VALUES(?,?,?,?)', ['4223949757774', 'Aziz Tea Black Tea 100 г', 'Aziz', 'Чай']);
  await db.close();
  return GlobalProducts(openFile: () async => f, executorFor: NativeDatabase.new);
}

/// На сколько прокрутить экран накладной, чтобы сверху остались «Итоги» и счётчик, а ниже — все строки.
const _rowsScroll = 215.0;

ParsedInvoice _invoice({List<InvoiceLine>? lines, double? total}) {
  final ls =
      lines ??
      const [
        InvoiceLine(row: 1, name: 'Адыгене 1 л', barcode: '4700000310326', unit: 'шт', qty: 24, cost: 38, sum: 912),
        InvoiceLine(row: 2, name: 'Сүт Айналайын 1 л (пакет)', unit: 'шт', qty: 12, cost: 62, sum: 744),
        InvoiceLine(row: 3, name: 'Чай Aziz чёрный 100 г', barcode: '4223949757774', unit: 'шт', qty: 10, cost: 62, sum: 620),
        InvoiceLine(row: 4, name: '', unit: 'шт', qty: 0, cost: 0),
      ];
  final sum = ls.where((l) => l.valid).fold<double>(0, (s, l) => s + l.lineSum());
  return ParsedInvoice(
    format: InvoiceFormat.xlsx,
    number: '417',
    date: DateTime(2026, 10, 28),
    supplierName: 'ОсОО «Нур Трейд»',
    supplierInn: '02305202010123',
    lines: ls,
    fileTotal: total ?? sum,
    currency: 'KGS',
    fileHash: 'ad-417',
  );
}

const _suppliers = [SupplierRef('s1', 'ОсОО «Нур Трейд»', '02305202010123')];

// ---------------------------------------------------------------------------------------------------------------- тесты

void main() {
  final shot = GlobalKey();

  setUpAll(() {
    if (_dir.isNotEmpty) Directory(_dir).createSync(recursive: true);
  });

  // ------------------------------------------------------------------------------------------ A. умная камера с реквизитом
  testWidgets('реклама: умная камера «По виду» на реквизите (18-mobile-cam-prop)', (t) async {
    await t.runAsync(_fonts);
    _phone(t);
    final stage = ValueNotifier<int>(0);
    await t.pumpWidget(RepaintBoundary(key: shot, child: _app('ru', _SmartCamProp(stage))));
    await t.pump(const Duration(milliseconds: 100));
    final v = _Video('18-mobile-cam-prop');
    await v.steps(t, shot, 14); // наведение: рамка крупная
    stage.value = 1; // «Сканировать»
    await v.steps(t, shot, 8);
    stage.value = 2; // рамка стягивается, ИИ проверяет
    await v.steps(t, shot, 9);
    stage.value = 3; // результат и «Не тот товар»
    await v.steps(t, shot, 6);
    await v.steps(t, shot, 36);
    expect(t.takeException(), isNull);
    v.finish();
    await t.pumpWidget(const SizedBox());
  }, skip: _dir.isEmpty);

  // ------------------------------------------------------------------------------------------ B. настоящий SmartCameraScreen
  testWidgets('реклама: умная камера — не уверен, варианты (18-mobile-cam-pick)', (t) async {
    await t.runAsync(_fonts);
    _phone(t);
    final s = _CamStand();
    await s.fill(t);
    final cands = [
      (_p('p2', 'ASU Негаз 1 л клуб-киви', price: 65, qty: 24), 0.8),
      (_p('p3', 'ASU Негаз 1л персик', price: 65, qty: 18), 0.78),
      (_p('p4', 'ASU Негаз 0,5 л персик', price: 45, qty: 30), 0.5),
      (_p('p1', 'Адыгене 1л', price: 50, qty: 24), 0.4),
    ];
    await s.pump(t, mode: 'shelf', matches: [SeenMatch(const SeenItem(brand: 'ASU', name: 'Still water', size: '1 L', count: 3), cands)]);
    await s.settle(t, 5);
    expect(find.text('Похоже на — выберите, если это он'), findsOneWidget);
    // прокрутка: заголовок «Похоже на» и варианты — в верхней части экрана
    await t.drag(find.byType(ListView).first, const Offset(0, -260));
    await t.pump(const Duration(milliseconds: 400));
    expect(t.takeException(), isNull);
    await _save(t, s.shot, '18-mobile-cam-pick');
    // нажали первого кандидата → «есть в базе», кнопка «Не тот товар» и запомненные названия
    await t.drag(find.byType(ListView).first, const Offset(0, 260));
    await t.pump(const Duration(milliseconds: 300));
    await t.tap(find.byKey(const ValueKey('cand_p2')));
    await s.settle(t);
    expect(find.byKey(const ValueKey('cand_p2')), findsNothing);
    expect(find.text('Не тот товар'), findsOneWidget);
    expect(t.takeException(), isNull);
    await _save(t, s.shot, '18-mobile-cam-picked');
    await t.pumpWidget(const SizedBox());
    await t.pump(const Duration(seconds: 5));
  }, skip: _dir.isEmpty);

  testWidgets('реклама: умная камера — накладная, «Из галереи» и «Из файла» (18-mobile-cam-invoice)', (t) async {
    await t.runAsync(_fonts);
    _phone(t);
    final s = _CamStand();
    await s.fill(t);
    await s.pump(t, initialMode: 'invoice');
    await s.settle(t, 3);
    expect(find.text('Из галереи'), findsOneWidget);
    expect(find.text('Из файла'), findsOneWidget);
    expect(t.takeException(), isNull);
    await _save(t, s.shot, '18-mobile-cam-invoice');
    await t.pumpWidget(const SizedBox());
    await t.pump(const Duration(seconds: 5));
  }, skip: _dir.isEmpty);

  testWidgets('реклама: умная камера — накладная из галереи, результат (18-mobile-cam-invoice-result)', (t) async {
    await t.runAsync(_fonts);
    _phone(t);
    final s = _CamStand();
    await s.fill(t);
    await s.pump(
      t,
      mode: 'invoice',
      ai: const [SeenItem(name: 'Адыгене 1л', count: 3)],
      images: () async => [CamPicked(Uint8List(100), 'a.jpg'), CamPicked(Uint8List(200), 'b.png', mime: 'image/png')],
    );
    await t.tap(find.byKey(const Key('cam_gallery')));
    await s.settle(t);
    expect(find.text('Адыгене 1л'), findsOneWidget);
    expect(find.text('6'), findsOneWidget, reason: '3 + 3 штуки одного товара');
    expect(t.takeException(), isNull);
    await t.drag(find.byType(ListView).first, const Offset(0, -120));
    await t.pump(const Duration(milliseconds: 400));
    await _save(t, s.shot, '18-mobile-cam-invoice-result');
    await t.pumpWidget(const SizedBox());
    await t.pump(const Duration(seconds: 5));
  }, skip: _dir.isEmpty);

  // ------------------------------------------------------------------------------------------ C1. видеозвонок: удаление товара
  testWidgets('реклама: видеозвонок — удалить товар в два шага (19-mobile-call-delete)', (t) async {
    await t.runAsync(_fonts);
    _phone(t);
    final dir = Directory.systemTemp.createTempSync('nm_ad_del_');
    Audit.testDirectory = dir;
    AiJournal.testDirectory = dir;
    addTearDown(() {
      try {
        dir.deleteSync(recursive: true);
      } catch (_) {}
    });
    // настоящий инструмент delete_product на фейковом каталоге: обе реплики программы берём из него
    final real = _shopRig();
    late final ToolResult ask, done;
    late final CallTable card;
    await t.runAsync(() async {
      ask = await real.say('Удали товар Семечки', 'delete_product', {'query': 'Семечки'}, _owner);
      card = real.tables.current!;
      done = await real.say('Да, удалить', 'delete_product', {'query': 'Семечки', 'confirm': true}, _owner);
    });
    expect(ask.status, ToolStatus.clarify);
    expect(done.status, ToolStatus.ok);
    expect(real.host.deleted, ['n3']);

    final rig = CallRig(lang: 'ru', session: _owner);
    await t.pumpWidget(RepaintBoundary(key: shot, child: _callApp(rig)));
    await rig.startCall(t);
    await rig.showFull(t);
    // инструмент-обёртка: возвращает готовые результаты настоящего delete_product; карточку кладёт в таблицы звонка
    var step = 0;
    rig.c.registry!.register(
      AssistantTool(
        decl: const ToolDecl('delete_product_ad', 'ad'),
        group: ToolGroup.money,
        gateFor: (_) => const [GateReq(words: Intents.delete)],
        run: (r) async {
          if (step++ == 0) {
            rig.c.tables.add(card);
            return ask;
          }
          return done;
        },
      ),
    );
    final v = _Video('19-mobile-call-delete');
    await v.steps(t, shot, 8);
    rig.socket.said('Удали');
    await v.steps(t, shot, 3);
    rig.socket.said(' товар Семечки');
    await v.steps(t, shot, 6);
    rig.socket.toolCall('d1', 'delete_product_ad');
    for (var i = 0; i < 3; i++) {
      await rig.settle(t, 2);
      await v.step(t, shot);
    }
    rig.socket.aiSaid(ask.say);
    await v.steps(t, shot, 30);
    // «да, удалить»
    rig.socket.turnComplete();
    rig.socket.said('Да,');
    await v.steps(t, shot, 3);
    rig.socket.said(' удалить');
    await v.steps(t, shot, 5);
    rig.socket.toolCall('d2', 'delete_product_ad');
    for (var i = 0; i < 3; i++) {
      await rig.settle(t, 2);
      await v.step(t, shot);
    }
    rig.socket.aiSaid(done.say);
    await v.steps(t, shot, 24);
    expect(t.takeException(), isNull);
    expect(rig.socket.responseOf('d2')?['status'], 'ok', reason: '${rig.socket.responseOf('d2')}');
    v.finish();
    await rig.dispose(t);
    await t.pumpWidget(const SizedBox());
  }, skip: _dir.isEmpty);

  // ------------------------------------------------------------------------------------------ C2. видеозвонок: мастер «добавить товар»
  testWidgets('реклама: видеозвонок — мастер «добавить товар» (19-mobile-call-add)', (t) async {
    await t.runAsync(_fonts);
    _phone(t);
    final dir = Directory.systemTemp.createTempSync('nm_ad_wiz_');
    Audit.testDirectory = dir;
    AiJournal.testDirectory = dir;
    addTearDown(() {
      try {
        dir.deleteSync(recursive: true);
      } catch (_) {}
    });
    final backend = _WizBackend();
    final rig = CallRig(lang: 'ru', camera: true, session: _owner, wizardBackend: backend);
    final stills = (await t.runAsync(() => Future.wait([for (var i = 0; i < 4; i++) _stillPng(i)])))!;
    rig.frames.still0 = stills[0];
    await t.pumpWidget(RepaintBoundary(key: shot, child: _callApp(rig)));
    await rig.startCall(t);
    await rig.showFull(t);
    await t.runAsync(() => VideoConsent.accept(rig.db, 'u1'));
    final l = lookupAppLocalizations(const Locale('ru'));
    await t.tap(find.text(l.vcCamera));
    await t.pump(const Duration(milliseconds: 700));
    final v = _Video('19-mobile-call-add');
    var n = 0;
    // реплика пользователя → вызов инструмента → фраза программы вслух (текст берётся из ответа настоящего инструмента)
    Future<String> turn(String said, Map<String, dynamic> args, {int look = 2, int hold = 4, String tool = 'product_wizard'}) async {
      rig.socket.turnComplete();
      rig.socket.said(said);
      await v.steps(t, shot, 2);
      final id = 'w${++n}';
      rig.socket.toolCall(id, tool, args);
      for (var i = 0; i < 2; i++) {
        await rig.settle(t, 2);
        await v.step(t, shot);
      }
      final say = '${rig.socket.responseOf(id)?['say'] ?? ''}';
      expect(say, isNotEmpty, reason: '$tool $args: ${rig.socket.responseOf(id)}');
      rig.socket.aiSaid(say);
      await v.steps(t, shot, look);
      await v.step(t, shot, hold: hold);
      return say;
    }

    await v.steps(t, shot, 3);
    await turn('Добавь новый товар', {'op': 'start'}, hold: 10);
    rig.frames.still0 = stills[0];
    await turn('Готово', {'op': 'photo'}, look: 1, hold: 6);
    rig.frames.still0 = stills[1];
    await turn('Готово', {'op': 'photo'}, look: 0, hold: 1);
    rig.frames.still0 = stills[2];
    await turn('Сбоку', {'op': 'photo'}, look: 0, hold: 3);
    rig.frames.still0 = stills[3];
    rig.frames.stillBarcode = '4223949757774';
    await turn('Вот штрихкод', {'op': 'photo', 'side': 'barcode'}, look: 1, hold: 12);
    await turn('Цена семьдесят восемь, закупка шестьдесят два, остаток двадцать', {'op': 'set', 'price': 78, 'purchase': 62, 'qty': 20}, look: 1, hold: 8);
    await turn('Да, создавай', {}, tool: 'wizard_create', hold: 14);
    expect(backend.created, hasLength(1));
    expect(t.takeException(), isNull);
    v.finish();
    await rig.dispose(t);
    await t.pumpWidget(const SizedBox());
  }, skip: _dir.isEmpty);

  // ------------------------------------------------------------------------------------------ C3. «Действия ИИ»
  testWidgets('реклама: Действия ИИ — несколько записей (19-mobile-ai-log)', (t) async {
    await t.runAsync(_fonts);
    _phone(t);
    final dir = Directory.systemTemp.createTempSync('nm_ad_log2_');
    Audit.testDirectory = dir;
    AiJournal.testDirectory = dir;
    addTearDown(() {
      try {
        dir.deleteSync(recursive: true);
      } catch (_) {}
    });
    final journal = AiJournal();
    final rig = _shopRig(journal: journal);
    await t.runAsync(() async {
      AiJournal.resetQueueForTests();
      Audit.resetForTests();
      DateTime ago(int m) => DateTime.now().subtract(Duration(minutes: m));
      // 1) цена Адыгене 50 → 55 и тут же «Отмени» (запись «отменено»)
      rig.now = ago(95);
      await rig.say('Поставь Адыгене 1,5 литра по 55', 'set_price', {'query': 'Адыгене', 'price': 55}, _owner);
      rig.now = ago(93);
      await rig.say('Отмени', 'undo_last', const {}, _owner);
      // 2) цена Нан 30 → 28
      rig.now = ago(41);
      await rig.say('Поменяй цену на хлеб на 28', 'set_price', {'query': 'Нан', 'price': 28}, _owner);
      // 3) минимальный остаток
      rig.now = ago(17);
      final minq = await rig.say('Поставь минимальный остаток для Нан 10', 'product_edit', {'query': 'Нан', 'min_qty': 10}, _owner);
      expect(minq.status, ToolStatus.ok, reason: minq.say);
      // 4) удаление товара в два шага (отмены нет); второй стенд с тем же журналом: у первого после правок удаление зависает
      final rig2 = _shopRig(journal: journal);
      rig2.now = ago(6);
      await rig2.say('Удали товар Семечки', 'delete_product', {'query': 'Семечки'}, _owner);
      rig2.now = ago(5);
      final del = await rig2.say('Да, удалить', 'delete_product', {'query': 'Семечки', 'confirm': true}, _owner);
      expect(del.status, ToolStatus.ok, reason: del.say);
    });
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
    for (var i = 0; i < 200; i++) {
      await _realDelay(t, 50);
      await t.pump(const Duration(milliseconds: 50));
      if (i >= 2 && find.byType(CircularProgressIndicator).evaluate().isEmpty) break;
    }
    await t.pump(const Duration(milliseconds: 400));
    expect(t.takeException(), isNull);
    await _save(t, shot, '19-mobile-ai-log');
    await t.pumpWidget(const SizedBox());
    await t.pump(const Duration(seconds: 5));
  }, skip: _dir.isEmpty);

  // ------------------------------------------------------------------------------------------ D. загрузка накладной
  Future<void> openInvoice(WidgetTester t, AppDatabase db, GlobalProducts cat, ParsedInvoice inv) async {
    await t.pumpWidget(
      RepaintBoundary(
        key: shot,
        child: ProviderScope(
          key: UniqueKey(),
          overrides: [databaseProvider.overrideWithValue(db), apiProvider.overrideWithValue(_InvApi()), globalProductsProvider.overrideWithValue(cat)],
          child: MaterialApp(
            debugShowCheckedModeBanner: false,
            theme: buildTheme(dark: false),
            locale: const Locale('ru'),
            supportedLocales: AppLocalizations.supportedLocales,
            localizationsDelegates: _delegates,
            home: InvoiceImportScreen(debugInvoice: inv, debugSuppliers: _suppliers),
          ),
        ),
      ),
    );
    for (var i = 0; i < 60; i++) {
      await _realDelay(t, 50);
      await t.pump();
      if (find.textContaining('Строк:').evaluate().isNotEmpty) break;
    }
    await t.pump(const Duration(milliseconds: 400));
  }

  Future<AppDatabase> invDb(WidgetTester t) async {
    final db = AppDatabase(NativeDatabase.memory());
    await t.runAsync(() async {
      InvoiceGuard.resetPending();
      Future<void> add(String id, String name, {String? barcode, double price = 0, double qty = 24, String unit = 'шт'}) => db.upsertProduct(
        ProductsCompanion.insert(id: id, name: name, barcode: drift.Value(barcode), unit: drift.Value(unit), price: drift.Value(price), quantity: drift.Value(qty), searchKey: drift.Value(name.toLowerCase())),
      );
      await add('p1', 'Адыгене 1 л', barcode: '4700000310326', price: 50);
      await add('p2', 'Сүт Айналайын 1 л', price: 75, qty: 12);
      await add('p3', 'Адыгене 1,5 л', price: 55);
    });
    return db;
  }

  testWidgets('реклама: накладная — найден по ИНН (20-mobile-invoice-inn, -rows)', (t) async {
    await t.runAsync(_fonts);
    _phone(t);
    final dir = Directory.systemTemp.createTempSync('nm_ad_inv_');
    addTearDown(() {
      try {
        dir.deleteSync(recursive: true);
      } catch (_) {}
    });
    final db = await invDb(t);
    final cat = (await t.runAsync(() => _catalog(dir)))!;
    await openInvoice(t, db, cat, _invoice());
    final l = lookupAppLocalizations(const Locale('ru'));
    expect(find.text(l.invSupByInn), findsOneWidget);
    expect(find.text(l.invDocLine('417', '28.10.2026')), findsOneWidget);
    expect(t.takeException(), isNull);
    await _save(t, shot, '20-mobile-invoice-inn');
    await t.pumpWidget(const SizedBox());
    await t.pump(const Duration(seconds: 1));
  }, skip: _dir.isEmpty);

  testWidgets('реклама: накладная — строки со статусами (20-mobile-invoice-rows)', (t) async {
    await t.runAsync(_fonts);
    _phone(t, dpr: 1.7); // строки накладной высокие: 635x1376 dp на тех же 1080x2340 px, чтобы поместились все четыре
    final dir = Directory.systemTemp.createTempSync('nm_ad_inv4_');
    addTearDown(() {
      try {
        dir.deleteSync(recursive: true);
      } catch (_) {}
    });
    final db = await invDb(t);
    final cat = (await t.runAsync(() => _catalog(dir)))!;
    await openInvoice(t, db, cat, _invoice());
    final l = lookupAppLocalizations(const Locale('ru'));
    expect(find.text(l.invStBarcode), findsOneWidget);
    expect(find.textContaining('Похоже на:'), findsOneWidget);
    expect(find.text(l.invStCatalog), findsOneWidget);
    expect(find.text(l.invTotalOk), findsOneWidget);
    await t.drag(find.byType(ListView).first, Offset(0, -_rowsScroll));
    await t.pump(const Duration(milliseconds: 500));
    expect(find.text(l.invStBad), findsOneWidget);
    expect(t.takeException(), isNull);
    await _save(t, shot, '20-mobile-invoice-rows');
    await t.pumpWidget(const SizedBox());
    await t.pump(const Duration(seconds: 1));
  }, skip: _dir.isEmpty);

  testWidgets('реклама: накладная — уже загружалась (20-mobile-invoice-dup)', (t) async {
    await t.runAsync(_fonts);
    _phone(t);
    final dir = Directory.systemTemp.createTempSync('nm_ad_inv2_');
    addTearDown(() {
      try {
        dir.deleteSync(recursive: true);
      } catch (_) {}
    });
    final db = await invDb(t);
    final cat = (await t.runAsync(() => _catalog(dir)))!;
    final inv = _invoice();
    await t.runAsync(() => InvoiceGuard.markPosted(db, InvoiceGuard.keys(inv), number: '417', supplier: 'ОсОО «Нур Трейд»'));
    await openInvoice(t, db, cat, inv);
    final l = lookupAppLocalizations(const Locale('ru'));
    expect(find.text(l.invDupPosted), findsOneWidget);
    expect(find.text(l.invDupAnyway), findsOneWidget);
    expect(t.takeException(), isNull);
    await _save(t, shot, '20-mobile-invoice-dup');
    await t.pumpWidget(const SizedBox());
    await t.pump(const Duration(seconds: 1));
  }, skip: _dir.isEmpty);

  testWidgets('реклама: накладная — упаковки, пересчёт в штуки (20-mobile-invoice-pack)', (t) async {
    await t.runAsync(_fonts);
    _phone(t);
    final dir = Directory.systemTemp.createTempSync('nm_ad_inv3_');
    addTearDown(() {
      try {
        dir.deleteSync(recursive: true);
      } catch (_) {}
    });
    final db = await invDb(t);
    final cat = (await t.runAsync(() => _catalog(dir)))!;
    final inv = _invoice(
      lines: const [
        InvoiceLine(row: 1, name: 'Адыгене 1,5 л', unit: 'кор', qty: 4, cost: 912, sum: 3648),
        InvoiceLine(row: 2, name: 'Адыгене 1 л', barcode: '4700000310326', unit: 'шт', qty: 24, cost: 38, sum: 912),
      ],
    );
    await openInvoice(t, db, cat, inv);
    final l = lookupAppLocalizations(const Locale('ru'));
    expect(find.text(l.invPackWarn('кор', 'Адыгене 1,5 л', 'шт')), findsOneWidget);
    await t.drag(find.byType(ListView).first, const Offset(0, -330));
    await t.pump(const Duration(milliseconds: 500));
    await _save(t, shot, '20-mobile-invoice-pack-ask');
    await t.enterText(find.byKey(const ValueKey('packN_1')), '24');
    await t.tap(find.byKey(const ValueKey('packPieces_1')));
    await t.pump(const Duration(milliseconds: 600));
    expect(find.textContaining('1 упак. = 24 шт'), findsOneWidget);
    expect(t.takeException(), isNull);
    await _save(t, shot, '20-mobile-invoice-pack');
    await t.pumpWidget(const SizedBox());
    await t.pump(const Duration(seconds: 1));
  }, skip: _dir.isEmpty);
}
