import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/features/scan/repeat_scan_dialog.dart';
import 'package:nurmarket/features/scan/repeat_scan_guard.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

// 2026-11-07 (владелец: «если товар смотрит долго, то подряд добавляет — при повторном скане останавливать сканер и спрашивать
// количество»): логика повтора (часы подменены, камера и сеть не нужны) и окно «Сколько штук?».
void main() {
  late DateTime now;
  late RepeatScanGuard g;
  const eur = '4870202521261';

  setUp(() {
    now = DateTime(2026, 11, 7, 10);
    g = RepeatScanGuard(clock: () => now);
  });

  void advance(int ms) => now = now.add(Duration(milliseconds: ms));

  /// Камера держит код [code] [ms] мс: кадры каждые 100 мс. Возвращает последний вердикт.
  ScanVerdict hold(String code, int ms) {
    var v = ScanVerdict.fresh;
    for (var t = 0; t < ms; t += 100) {
      advance(100);
      v = g.check(code);
    }
    return v;
  }

  group('RepeatScanGuard', () {
    test('первый скан — новый; кадры только что принятой пачки — «тот же скан», не повтор', () {
      expect(g.check(eur), ScanVerdict.fresh);
      g.counted(eur);
      for (var i = 0; i < 10; i++) {
        advance(100);
        expect(g.check(eur), ScanVerdict.same, reason: '${(i + 1) * 100} мс');
      }
    });

    test('тот же код держат в кадре дольше 1,5 с — повтор (а не ещё одна штука молча)', () {
      expect(g.check(eur), ScanVerdict.fresh);
      g.counted(eur);
      expect(hold(eur, 1400), ScanVerdict.same);
      advance(100);
      expect(g.check(eur), ScanVerdict.repeat);
    });

    test('тот же код сразу после добавления с коротким пропаданием кадра (до 0,7 с) — всё ещё тот же скан', () {
      g.check(eur);
      g.counted(eur);
      advance(300);
      expect(g.check(eur), ScanVerdict.same);
      advance(600); // пропал на 0,6 с (автофокус) и вернулся
      expect(g.check(eur), ScanVerdict.same);
    });

    test('тот же код вернули через 0,7–1,5 с (быстро поднесли вторую такую же пачку) — повтор', () {
      g.check(eur);
      g.counted(eur);
      advance(1000);
      expect(g.check(eur), ScanVerdict.repeat);
    });

    test('другой код — не повтор; и прежний код после другого — снова новый', () {
      g.check(eur);
      g.counted(eur);
      advance(200);
      expect(g.check('111222333'), ScanVerdict.fresh);
      g.counted('111222333');
      advance(200);
      expect(g.check(eur), ScanVerdict.fresh);
    });

    test('разные коды подряд без задержек: каждый новый', () {
      for (var i = 0; i < 5; i++) {
        final c = '48702025212${i}1';
        expect(g.check(c), ScanVerdict.fresh);
        g.counted(c);
        advance(150);
      }
    });

    test('тот же код после паузы без кода ≥ 1,5 с — не повтор, а новый скан', () {
      g.check(eur);
      g.counted(eur);
      advance(1500);
      expect(g.check(eur), ScanVerdict.fresh);
      g.counted(eur);
      advance(5000); // долго не было кода
      expect(g.check(eur), ScanVerdict.fresh);
    });

    test('после закрытия окна: пока код в кадре — игнор (даже дольше 1,5 с), после отвода камеры на 1,5 с — новый', () {
      g.check(eur);
      g.counted(eur);
      expect(hold(eur, 1600), ScanVerdict.repeat);
      g.dialogClosed(eur); // окно закрыто («Добавить» или «Отмена»), камера включилась снова
      expect(g.locked, isTrue);
      expect(hold(eur, 5000), ScanVerdict.same); // товар так и лежит перед камерой — окно второй раз не открывается
      advance(1400);
      expect(g.check(eur), ScanVerdict.same); // камеру отвели на 1,4 с — рано
      advance(1500);
      expect(g.check(eur), ScanVerdict.fresh); // отвели на 1,5 с — код снова новый
      expect(g.locked, isFalse);
    });

    test('после закрытия окна другой товар считается сразу', () {
      g.check(eur);
      g.counted(eur);
      g.dialogClosed(eur);
      advance(100);
      expect(g.check('999'), ScanVerdict.fresh);
    });

    test('весовая этикетка: каждая новая этикетка (код с весом) — новый скан; та же этикетка — повтор', () {
      const a = '2000012003458'; // 0,345 кг
      const b = '2000012005678'; // 0,567 кг
      expect(g.check(a), ScanVerdict.fresh);
      g.counted(a);
      advance(300);
      expect(g.check(b), ScanVerdict.fresh);
      g.counted(b);
      expect(hold(b, 1600), ScanVerdict.repeat);
    });

    test('reset: следующий код новый', () {
      g.check(eur);
      g.counted(eur);
      advance(200);
      g.reset();
      expect(g.check(eur), ScanVerdict.fresh);
    });

    test('пока код не засчитан (разбор не принял) — вердикт «новый»', () {
      expect(g.check(eur), ScanVerdict.fresh);
      advance(2000);
      expect(g.check(eur), ScanVerdict.fresh);
    });

    test('свои пороги: gone/settle', () {
      final h = RepeatScanGuard(clock: () => now, gone: const Duration(seconds: 3), settle: const Duration(seconds: 2));
      h.check(eur);
      h.counted(eur);
      advance(100);
      expect(h.check(eur), ScanVerdict.same);
      for (var t = 0; t < 2000; t += 100) {
        advance(100);
        h.check(eur);
      }
      advance(100);
      expect(h.check(eur), ScanVerdict.repeat);
      advance(2900);
      expect(h.check(eur), ScanVerdict.repeat);
      advance(3000);
      expect(h.check(eur), ScanVerdict.fresh);
    });
  });

  group('окно «Сколько штук?»', () {
    Future<void> open(WidgetTester t, {bool fractional = false, double initial = 1, double? max, String lang = 'ru', List<double?>? sink}) async {
      await t.pumpWidget(
        MaterialApp(
          locale: Locale(lang),
          supportedLocales: AppLocalizations.supportedLocales,
          localizationsDelegates: const [AppLocalizations.delegate, GlobalMaterialLocalizations.delegate, GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate],
          home: Builder(
            builder: (c) => Scaffold(
              body: TextButton(
                onPressed: () async {
                  final r = await askRepeatQty(
                    c,
                    name: 'Mars',
                    message: repeatQtyMessage(c, RepeatQtyKind.check, have: 1, unit: 'шт'),
                    unit: 'шт',
                    initial: initial,
                    fractional: fractional,
                    max: max,
                  );
                  sink?.add(r);
                },
                child: const Text('go'),
              ),
            ),
          ),
        ),
      );
      await t.tap(find.text('go'));
      await t.pumpAndSettle();
    }

    testWidgets('название, «Уже в чеке: 1 шт. Сколько добавить?», кнопки; «Добавить» отдаёт количество', (t) async {
      final out = <double?>[];
      await open(t, sink: out);
      expect(find.text('Mars'), findsOneWidget);
      expect(find.text('Уже в чеке: 1 шт. Сколько добавить?'), findsOneWidget);
      await t.tap(find.byKey(const Key('repeatQtyAdd')));
      await t.pumpAndSettle();
      expect(out, [1.0]);
    });

    testWidgets('«+» и «−» меняют число, ввод цифрами, «Добавить» берёт его', (t) async {
      final out = <double?>[];
      await open(t, sink: out);
      await t.tap(find.byIcon(Icons.add_rounded));
      await t.tap(find.byIcon(Icons.add_rounded));
      await t.pump();
      expect(find.widgetWithText(TextField, '3'), findsOneWidget);
      await t.tap(find.byIcon(Icons.remove_rounded));
      await t.pump();
      expect(find.widgetWithText(TextField, '2'), findsOneWidget);
      await t.enterText(find.byKey(const Key('repeatQtyField')), '12');
      await t.tap(find.byKey(const Key('repeatQtyAdd')));
      await t.pumpAndSettle();
      expect(out, [12.0]);
    });

    testWidgets('«Отмена» — null, ничего не добавляется', (t) async {
      final out = <double?>[];
      await open(t, sink: out);
      await t.tap(find.byKey(const Key('repeatQtyCancel')));
      await t.pumpAndSettle();
      expect(out, [null]);
    });

    testWidgets('«−» не уходит ниже одной штуки; пустое поле — «Добавить» недоступна', (t) async {
      await open(t);
      await t.tap(find.byIcon(Icons.remove_rounded));
      await t.pump();
      expect(find.widgetWithText(TextField, '1'), findsOneWidget);
      await t.enterText(find.byKey(const Key('repeatQtyField')), '');
      await t.pump();
      expect(t.widget<FilledButton>(find.byKey(const Key('repeatQtyAdd'))).onPressed, isNull);
    });

    testWidgets('весовой товар: дробное количество', (t) async {
      final out = <double?>[];
      await open(t, fractional: true, initial: 0.345, sink: out);
      await t.enterText(find.byKey(const Key('repeatQtyField')), '0,75');
      await t.tap(find.byKey(const Key('repeatQtyAdd')));
      await t.pumpAndSettle();
      expect(out, [0.75]);
    });

    testWidgets('штучный товар: дробная точка не вводится (только цифры)', (t) async {
      final out = <double?>[];
      await open(t, sink: out);
      await t.enterText(find.byKey(const Key('repeatQtyField')), '2.5');
      await t.tap(find.byKey(const Key('repeatQtyAdd')));
      await t.pumpAndSettle();
      expect(out, [25.0]); // «.» отброшен фильтром цифр — число остаётся целым
    });

    testWidgets('max: не больше недобранного', (t) async {
      final out = <double?>[];
      await open(t, max: 3, sink: out);
      await t.enterText(find.byKey(const Key('repeatQtyField')), '10');
      await t.tap(find.byKey(const Key('repeatQtyAdd')));
      await t.pumpAndSettle();
      expect(out, [3.0]);
    });

    for (final lang in ['ru', 'ky', 'uz', 'en', 'tr']) {
      testWidgets('надписи на языке $lang', (t) async {
        await open(t, lang: lang);
        final msg = <String, String>{
          'ru': 'Уже в чеке: 1 шт. Сколько добавить?',
          'ky': 'Чекте бар: 1 шт. Канчасын кошосуз?',
          'uz': "Chekda bor: 1 шт. Nechta qo'shiladi?",
          'en': 'Already in the receipt: 1 шт. How many to add?',
          'tr': 'Fişte zaten var: 1 шт. Kaç tane eklensin?',
        };
        expect(find.text(msg[lang]!), findsOneWidget);
      });
    }
  });
}
