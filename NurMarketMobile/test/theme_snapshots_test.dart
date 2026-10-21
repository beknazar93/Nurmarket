// 2026-10-21: снимки общей темы — текстовые кнопки («Отмена»), подпись активного поля, значки чипов, вкладки, календарь —
// для сверки «жёлтый текст на белом». Без --dart-define=SNAP_DIR=<папка> тест пропускается, в репозиторий ничего не пишется.
// Пример: flutter test test/theme_snapshots_test.dart --dart-define=SNAP_DIR=C:/tmp/snaps --dart-define=SNAP_TAG=after
import 'dart:io';
import 'dart:ui' as ui;

import 'package:flutter/material.dart';
import 'package:flutter/rendering.dart';
import 'package:flutter/services.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/theme/theme.dart';
import 'package:nurmarket/core/util/ui.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

const _dir = String.fromEnvironment('SNAP_DIR');
const _tag = String.fromEnvironment('SNAP_TAG', defaultValue: 'after');

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

/// Витрина: то, что тема красит цветом акцента по умолчанию.
class _Gallery extends StatefulWidget {
  const _Gallery();
  @override
  State<_Gallery> createState() => _GalleryState();
}

class _GalleryState extends State<_Gallery> with SingleTickerProviderStateMixin {
  late final _tabs = TabController(length: 3, vsync: this);
  final _focus = FocusNode();
  final _ctrl = TextEditingController(text: 'ОсОО «Үмүт»');

  @override
  void dispose() {
    _tabs.dispose();
    _focus.dispose();
    _ctrl.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => Scaffold(
    appBar: AppBar(
      title: const Text('Тема'),
      bottom: TabBar(
        controller: _tabs,
        tabs: const [
          Tab(text: 'Все'),
          Tab(text: 'КП'),
          Tab(text: 'Счета'),
        ],
      ),
    ),
    body: ListView(
      padding: const EdgeInsets.all(16),
      children: [
        TextField(
          key: const Key('focused'),
          focusNode: _focus,
          controller: _ctrl,
          decoration: const InputDecoration(labelText: 'Компания', prefixIcon: Icon(Icons.business_rounded), suffixIcon: Icon(Icons.close_rounded)),
        ),
        const SizedBox(height: 12),
        const TextField(decoration: InputDecoration(labelText: 'Телефон')),
        const SizedBox(height: 12),
        Wrap(
          spacing: 8,
          children: [
            TextButton(onPressed: () {}, child: const Text('Отмена')),
            TextButton.icon(onPressed: () {}, icon: const Icon(Icons.open_in_new_rounded), label: const Text('Открыть ссылку')),
            FilledButton(onPressed: () {}, child: const Text('Сохранить')),
          ],
        ),
        const SizedBox(height: 8),
        Wrap(
          spacing: 6,
          children: [
            ActionChip(avatar: const Icon(Icons.add_rounded, size: 18), label: const Text('Добавить товар'), onPressed: () {}),
            ActionChip(avatar: const Icon(Icons.qr_code_scanner_rounded, size: 18), label: const Text('Сканер'), onPressed: () {}),
          ],
        ),
        ListTile(selected: true, leading: const Icon(Icons.description_outlined), title: const Text('Выбранная строка'), onTap: () {}),
        CheckboxListTile(value: true, onChanged: (_) {}, title: const Text('Галочка')),
        SwitchListTile(value: true, onChanged: (_) {}, title: const Text('Переключатель')),
        const ExpansionTile(initiallyExpanded: true, title: Text('Раскрытый раздел'), children: [ListTile(title: Text('Строка'))]),
        const Padding(padding: EdgeInsets.all(8), child: LinearProgressIndicator(value: .6)),
      ],
    ),
  );
}

void main() {
  final shot = GlobalKey();
  Widget app(Widget home, {bool dark = false}) => RepaintBoundary(
    key: shot,
    child: MaterialApp(
      debugShowCheckedModeBanner: false,
      theme: buildTheme(dark: false),
      darkTheme: buildTheme(dark: true),
      themeMode: dark ? ThemeMode.dark : ThemeMode.light,
      locale: const Locale('ru'),
      supportedLocales: AppLocalizations.supportedLocales,
      localizationsDelegates: const [AppLocalizations.delegate, GlobalMaterialLocalizations.delegate, GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate],
      home: home,
    ),
  );

  Future<void> save(WidgetTester t, String name) async {
    await t.runAsync(() async {
      final b = shot.currentContext!.findRenderObject()! as RenderRepaintBoundary;
      final img = await b.toImage(pixelRatio: 2);
      final png = await img.toByteData(format: ui.ImageByteFormat.png);
      File('$_dir/${_tag}_$name.png').writeAsBytesSync(png!.buffer.asUint8List());
    });
  }

  Future<void> phone(WidgetTester t) async {
    await t.runAsync(_fonts);
    t.view.physicalSize = const Size(780, 1688);
    t.view.devicePixelRatio = 2;
    addTearDown(t.view.reset);
  }

  for (final dark in [false, true]) {
    final sfx = dark ? '_dark' : '';
    testWidgets('снимок темы: витрина$sfx', (t) async {
      await phone(t);
      await t.pumpWidget(app(const _Gallery(), dark: dark));
      await t.pumpAndSettle();
      await t.tap(find.byKey(const Key('focused')));
      await t.pumpAndSettle();
      await save(t, 'gallery$sfx');
    }, skip: _dir.isEmpty);

    testWidgets('снимок темы: окно «Количество» с «Отмена»$sfx', (t) async {
      await phone(t);
      await t.pumpWidget(
        app(
          Builder(
            builder: (c) => Scaffold(
              body: Center(
                child: FilledButton(onPressed: () => askNumber(c, title: 'Вода «Нур» 1,5 л', hint: 'шт', initial: 2), child: const Text('Открыть')),
              ),
            ),
          ),
          dark: dark,
        ),
      );
      await t.tap(find.text('Открыть'));
      await t.pumpAndSettle();
      await save(t, 'ask_number$sfx');
    }, skip: _dir.isEmpty);

    testWidgets('снимок темы: календарь$sfx', (t) async {
      await phone(t);
      await t.pumpWidget(
        app(
          Builder(
            builder: (c) => Scaffold(
              body: Center(
                child: FilledButton(
                  onPressed: () {
                    final today = DateUtils.dateOnly(DateTime.now());
                    showDatePicker(context: c, initialDate: today.add(const Duration(days: 3)), firstDate: today.subtract(const Duration(days: 30)), lastDate: today.add(const Duration(days: 365)));
                  },
                  child: const Text('Открыть'),
                ),
              ),
            ),
          ),
          dark: dark,
        ),
      );
      await t.tap(find.text('Открыть'));
      await t.pumpAndSettle();
      await save(t, 'date_picker$sfx');
    }, skip: _dir.isEmpty);
  }
}
