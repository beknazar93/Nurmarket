import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/theme/theme.dart';
import 'package:nurmarket/features/scan/barcode_bind.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

// 2026-10-22, владелец: «умная камера после обучения выходит сканер, но он тупо ничего не говорит после фоток».
// Шаг «Штрихкод» после фото обучения: подсказка, «Пропустить», ввод вручную, что будет с кодом, чужой код.
Product _p(String id, String name, {String? barcode}) => Product(
  id: id,
  name: name,
  price: 10,
  wholesalePrice: 0,
  promoRules: '',
  minQuantity: 0,
  quantity: 5,
  unit: 'шт',
  isWeight: false,
  isService: false,
  isFavorite: false,
  altBarcodes: '',
  category: '',
  searchKey: name,
  barcode: barcode,
);

void main() {
  late List<Object?> results;
  setUp(() => results = []);

  Widget app(BarcodeBindPage page) => ProviderScope(
    child: MaterialApp(
      theme: buildTheme(dark: false),
      locale: const Locale('ru'),
      supportedLocales: AppLocalizations.supportedLocales,
      localizationsDelegates: const [AppLocalizations.delegate, GlobalMaterialLocalizations.delegate, GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate],
      home: Builder(
        builder: (c) => Scaffold(
          body: Center(
            child: ElevatedButton(
              onPressed: () async => results.add(await Navigator.of(c).push<BindResult>(MaterialPageRoute(builder: (_) => page))),
              child: const Text('open'),
            ),
          ),
        ),
      ),
    ),
  );

  BarcodeBindPage page({Product? owner, String? productId, String name = 'Вода «Нур»'}) => BarcodeBindPage(
    productName: name,
    productId: productId,
    preview: const ColoredBox(color: Colors.black),
    lookup: (_) async => (product: owner, unavailable: false),
  );

  Future<void> open(WidgetTester t, BarcodeBindPage p) async {
    await t.pumpWidget(app(p));
    await t.tap(find.text('open'));
    await t.pump();
    await t.pump(const Duration(milliseconds: 400));
  }

  BarcodeBindPageState state(WidgetTester t) => t.state<BarcodeBindPageState>(find.byType(BarcodeBindPage));

  testWidgets('после обучения: шаги, подсказка «теперь отсканируйте штрихкод» и «Пропустить»', (t) async {
    await open(t, page());
    expect(find.text('Фото сохранены ✓ — теперь отсканируйте штрихкод товара'), findsOneWidget);
    expect(find.text('Если на товаре нет штрихкода — нажмите «Пропустить»'), findsOneWidget);
    expect(find.text('Фото'), findsOneWidget);
    expect(find.text('Штрихкод'), findsOneWidget);
    expect(find.text('Готово'), findsOneWidget);
    expect(find.text('✓'), findsOneWidget, reason: 'шаг «Фото» сделан');
    expect(find.text('Ввести вручную'), findsOneWidget);
    final skip = find.text('Пропустить');
    expect(skip, findsOneWidget);
    expect(t.getSize(find.ancestor(of: skip, matching: find.byWidgetPredicate((w) => w is FilledButton))).height, greaterThanOrEqualTo(48));
    await t.tap(skip);
    await t.pump();
    await t.pump(const Duration(milliseconds: 400));
    expect(results, hasLength(1));
    final r = results.single as BindResult;
    expect(r.code, isNull);
    expect(r.takenFrom, isNull);
  });

  testWidgets('скан: код и что с ним будет, «Готово» возвращает код', (t) async {
    await open(t, page());
    await state(t).onCode('4600123456789');
    await t.pump();
    expect(state(t).phase, BindPhase.confirm);
    expect(find.text('Штрихкод 4600123456789 будет привязан к «Вода «Нур»»'), findsOneWidget);
    expect(find.text('Пропустить'), findsNothing);
    await t.tap(find.widgetWithText(FilledButton, 'Готово'));
    await t.pump();
    await t.pump(const Duration(milliseconds: 400));
    final r = results.single as BindResult;
    expect(r.code, '4600123456789');
    expect(r.takenFrom, isNull);
  });

  testWidgets('ввод вручную цифровой клавиатурой', (t) async {
    await open(t, page(name: ''));
    await t.tap(find.text('Ввести вручную'));
    await t.pump();
    await t.pump(const Duration(milliseconds: 300));
    final field = t.widget<TextField>(find.byType(TextField));
    expect(field.keyboardType, TextInputType.number);
    await t.enterText(find.byType(TextField), '4870001112223');
    await t.tap(find.widgetWithText(FilledButton, 'Готово'));
    await t.pump();
    await t.pump(const Duration(milliseconds: 300));
    expect(find.text('Штрихкод 4870001112223 будет привязан к новому товару'), findsOneWidget);
  });

  testWidgets('код уже у другого товара: «Заменить / Оставить / Отмена»', (t) async {
    final other = _p('o', 'Сок яблочный', barcode: '4600123456789');
    await open(t, page(owner: other, productId: 'me'));
    final pending = state(t).onCode('4600123456789');
    await t.pump();
    await t.pump(const Duration(milliseconds: 300));
    expect(find.text('Штрихкод уже у другого товара'), findsOneWidget);
    expect(find.text('Заменить'), findsOneWidget);
    expect(find.text('Оставить'), findsOneWidget);
    expect(find.text('Отмена'), findsOneWidget);
    // «Отмена» — снова сканирование
    await t.tap(find.text('Отмена'));
    await t.pump();
    await t.pump(const Duration(milliseconds: 300));
    await pending;
    expect(state(t).phase, BindPhase.scan);
    // тот же код сразу же снова в кадре — не спрашиваем
    await state(t).onCode('4600123456789');
    expect(state(t).phase, BindPhase.scan);
  });

  testWidgets('«Заменить» — код переходит этому товару (takenFrom), «Оставить» — без штрихкода', (t) async {
    final other = _p('o', 'Сок яблочный', barcode: '4600123456789');
    await open(t, page(owner: other, productId: 'me'));
    final pending = state(t).onCode('4600123456789');
    await t.pump();
    await t.pump(const Duration(milliseconds: 300));
    await t.tap(find.text('Заменить'));
    await t.pump();
    await t.pump(const Duration(milliseconds: 300));
    await pending;
    expect(state(t).phase, BindPhase.confirm);
    await t.tap(find.widgetWithText(FilledButton, 'Готово'));
    await t.pump();
    await t.pump(const Duration(milliseconds: 400));
    final r = results.single as BindResult;
    expect(r.code, '4600123456789');
    expect(r.takenFrom?.id, 'o');

    // новый товар (ещё не создан): «Заменить» нет, «Оставить» — без штрихкода
    results.clear();
    await open(t, page(owner: other));
    final p2 = state(t).onCode('4600123456789');
    await t.pump();
    await t.pump(const Duration(milliseconds: 300));
    expect(find.text('Заменить'), findsNothing);
    await t.tap(find.text('Оставить'));
    await t.pump();
    await t.pump(const Duration(milliseconds: 400));
    await p2;
    final r2 = results.single as BindResult;
    expect(r2.code, isNull);
  });

  testWidgets('свой же код — «менять ничего не нужно»', (t) async {
    await open(t, page(owner: _p('me', 'Вода «Нур»', barcode: '4600123456789'), productId: 'me'));
    await state(t).onCode('4600123456789');
    await t.pump();
    expect(find.text('Штрихкод 4600123456789 уже привязан к «Вода «Нур»» — менять ничего не нужно'), findsOneWidget);
  });
}
