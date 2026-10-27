// 2026-10-27 (владелец: «добавь в мобилку кнопку не только из камеры/сканера, но и из файла или из галереи»; на снимке
// подписи вкладок «Накладн/ая», «Этикетк/и» рвались посреди слова): «Умная камера» — вкладки Полка/Накладная/Этикетки
// не переносят слова ни в одном из 5 языков и при шрифте 1,3, «Из галереи» есть во всех режимах сразу под зоной снимка
// (видна без прокрутки на 360×640), «Из файла» — только в накладной; выбор из галереи идёт в разбор своего режима.
// Gemini не вызывается: ответы ИИ подставлены, сеть — фейк, системные окна выбора подменены.
import 'dart:typed_data';

import 'package:drift/native.dart';
import 'package:flutter/material.dart';
import 'package:flutter/rendering.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';
import 'package:nurmarket/core/ai/cam_aliases.dart';
import 'package:nurmarket/core/ai/vision.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/providers.dart';
import 'package:nurmarket/core/theme/theme.dart';
import 'package:nurmarket/features/scan/smart_camera.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

import 'admin_stock_fixes_test.dart' show FakeApi;

class _Vision extends VisionService {
  final calls = <({String mode, String mime})>[];
  _Vision(super.api, super.db);
  @override
  Future<String?> key() async => 'k';
  @override
  Future<List<SeenItem>> recognize(Uint8List jpeg, {String mode = 'sale', String mime = 'image/jpeg'}) async {
    calls.add((mode: mode, mime: mime));
    return const [SeenItem(name: 'Адыгене 1л', count: 1)];
  }
}

class _Fixed extends SessionController {
  final Session s;
  _Fixed(this.s);
  @override
  Session? build() => s;
}

const _owner = Session(email: 'a', access: '', refresh: '', role: 'owner', isOwner: true, perms: []);
const _modes = ['shelf', 'invoice', 'labels'];

void main() {
  late AppDatabase db;
  late FakeApi api;

  setUp(() {
    CamAliases.resetForTest();
    db = AppDatabase(NativeDatabase.memory());
    api = FakeApi((m, p, b, q) async {
      if (p == 'api/main/app-settings/nurmarket-mobile/') return {'settings': <String, dynamic>{}};
      if (m == 'GET' && p == 'api/main/products/list/') return {'results': []};
      throw ApiException('не ожидали $m $p', status: 404);
    })..attach(_owner);
  });
  tearDown(() => db.close());

  Future<_Vision> pump(
    WidgetTester tester, {
    String mode = 'shelf',
    Size size = const Size(360, 640),
    double scale = 1.0,
    Locale locale = const Locale('ru'),
    bool dark = false,
    Future<List<CamPicked>> Function()? images,
    Future<CamPicked?> Function()? file,
  }) async {
    tester.view.physicalSize = size;
    tester.view.devicePixelRatio = 1;
    tester.platformDispatcher.textScaleFactorTestValue = scale;
    addTearDown(tester.view.reset);
    addTearDown(tester.platformDispatcher.clearAllTestValues);
    final vision = _Vision(api, db);
    final router = GoRouter(
      routes: [GoRoute(path: '/', builder: (_, _) => SmartCameraScreen(debugMode: mode, debugPickImages: images, debugPickFile: file))],
    );
    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          databaseProvider.overrideWithValue(db),
          apiProvider.overrideWithValue(api),
          visionProvider.overrideWithValue(vision),
          sessionProvider.overrideWith(() => _Fixed(_owner)),
        ],
        child: MaterialApp.router(
          routerConfig: router,
          theme: buildTheme(dark: dark),
          locale: locale,
          supportedLocales: AppLocalizations.supportedLocales,
          localizationsDelegates: const [AppLocalizations.delegate, GlobalMaterialLocalizations.delegate, GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate],
        ),
      ),
    );
    await tester.pump();
    return vision;
  }

  Future<void> settle(WidgetTester tester) async {
    for (var i = 0; i < 25; i++) {
      await tester.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 40)));
      await tester.pump();
    }
  }

  void checkTabs(WidgetTester tester, double scale, String reason) {
    for (final m in _modes) {
      final tab = find.byKey(Key('cam_mode_$m'));
      expect(tab, findsOneWidget, reason: '$reason $m');
      final tabRect = tester.getRect(tab);
      expect(tabRect.height, greaterThanOrEqualTo(48), reason: '$reason $m: цель нажатия ≥ 48 dp');
      expect(tabRect.width, greaterThanOrEqualTo(48), reason: '$reason $m');
      final text = find.descendant(of: tab, matching: find.byType(Text));
      expect(text, findsOneWidget, reason: '$reason $m');
      final w = tester.widget<Text>(text);
      expect(w.maxLines, 1, reason: '$reason $m: подпись в одну строку');
      expect(w.softWrap, false, reason: '$reason $m: слово не переносится');
      // подпись целиком внутри вкладки (FittedBox уменьшил её, а не обрезал)
      final r = tester.getRect(text);
      expect(r.left, greaterThanOrEqualTo(tabRect.left - 0.5), reason: '$reason $m: влево');
      expect(r.right, lessThanOrEqualTo(tabRect.right + 0.5), reason: '$reason $m: вправо');
      // и не мельче ~8 sp даже в тестовом шрифте Ahem (там каждая буква — целый «em», реальный шрифт заметно уже: «Этикеткалар» на телефоне ≈ 13 sp)
      final natural = tester.renderObject<RenderParagraph>(text).size.height;
      final basePx = 13 * scale;
      expect(basePx * r.height / natural, greaterThanOrEqualTo(8), reason: '$reason $m: размер подписи');
    }
  }

  void checkButtons(WidgetTester tester, String mode, Size view, String reason) {
    final g = find.byKey(const Key('cam_gallery'));
    expect(g, findsOneWidget, reason: '$reason $mode');
    final gr = tester.getRect(g);
    expect(gr.height, greaterThanOrEqualTo(48), reason: '$reason $mode');
    // видна сразу, без прокрутки, и не заходит на зону снимка
    expect(gr.bottom, lessThanOrEqualTo(view.height), reason: '$reason $mode: ниже сгиба');
    expect(gr.top, greaterThanOrEqualTo(tester.getRect(find.byIcon(Icons.photo_camera_rounded)).bottom), reason: '$reason $mode: перекрывает зону снимка');
    final f = find.byKey(const Key('cam_file'));
    if (mode == 'invoice') {
      expect(f, findsOneWidget, reason: '$reason $mode');
      expect(tester.getRect(f).height, greaterThanOrEqualTo(48), reason: '$reason $mode');
      expect(tester.getRect(f).bottom, lessThanOrEqualTo(view.height), reason: '$reason $mode');
      expect(tester.getRect(f).left, greaterThanOrEqualTo(gr.right), reason: '$reason $mode: кнопки рядом, не друг на друге');
    } else {
      expect(f, findsNothing, reason: '$reason $mode: «Из файла» только в накладной');
    }
  }

  for (final size in const [Size(360, 800), Size(360, 640)]) {
    for (final scale in const [1.0, 1.3]) {
      for (final lang in const ['ru', 'ky', 'uz', 'en', 'tr']) {
        for (final dark in const [false, true]) {
          final name = '${size.width.toInt()}×${size.height.toInt()}, шрифт $scale, $lang, ${dark ? 'тёмная' : 'светлая'}';
          testWidgets('вкладки без переноса слов и кнопки видны: $name', (tester) async {
            await pump(tester, size: size, scale: scale, locale: Locale(lang), dark: dark);
            for (final m in _modes) {
              await tester.tap(find.byKey(Key('cam_mode_$m')));
              await tester.pump();
              expect(tester.takeException(), isNull, reason: '$name $m: переполнение');
              checkTabs(tester, scale, name);
              checkButtons(tester, m, size, name);
            }
          });
        }
      }
    }
  }

  testWidgets('«Из галереи» в каждом режиме идёт в разбор своего режима, «Из файла» — только в накладной', (tester) async {
    for (final m in _modes) {
      var picked = 0;
      final v = await pump(
        tester,
        mode: m,
        images: () async {
          picked++;
          return [CamPicked(Uint8List(100), 'a.jpg'), CamPicked(Uint8List(100), 'b.png', mime: 'image/png')];
        },
        file: () async => CamPicked(Uint8List(100), 'x.pdf', mime: 'application/pdf'),
      );
      await tester.tap(find.byKey(const Key('cam_gallery')));
      await settle(tester);
      expect(picked, 1, reason: m);
      expect(v.calls.map((c) => (c.mode, c.mime)), [(m, 'image/jpeg'), (m, 'image/png')], reason: '$m: тот же разбор, что у живого снимка');
      expect(find.text('Адыгене 1л'), findsOneWidget, reason: m);
      if (m == 'invoice') {
        await tester.tap(find.byKey(const Key('cam_file')));
        await settle(tester);
        expect(v.calls.last, (mode: 'invoice', mime: 'application/pdf'));
      } else {
        expect(find.byKey(const Key('cam_file')), findsNothing, reason: m);
      }
    }
  });

  testWidgets('переключение вкладок: «Из файла» и подсказка накладной появляются и пропадают', (tester) async {
    await pump(tester);
    expect(find.byKey(const Key('cam_file')), findsNothing);
    await tester.tap(find.byKey(const Key('cam_mode_invoice')));
    await tester.pump();
    expect(find.byKey(const Key('cam_file')), findsOneWidget);
    await tester.tap(find.byKey(const Key('cam_mode_labels')));
    await tester.pump();
    expect(find.byKey(const Key('cam_file')), findsNothing);
    expect(find.byKey(const Key('cam_gallery')), findsOneWidget);
  });
}
